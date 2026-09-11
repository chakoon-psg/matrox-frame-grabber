using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The kinds of screen fault this app is for, from CONTEXT.md.
    ///
    /// All of them are here even though only <see cref="Dropout"/> is implemented, because the
    /// settings file needs a key per kind and the settings window needs to list what is *not* being
    /// checked - a quiet panel must not read as "nothing wrong" when one of several things is being
    /// looked at. Whether a kind has a detector is <see cref="AnomalyCatalog.Implemented"/>, not a
    /// property of the enum.
    ///
    /// Numbers are the settings file's keys, so they only ever get appended to. Two were appended:
    /// <see cref="Freeze"/> and <see cref="ColorShift"/> were both already named in CONTEXT.md as
    /// faults that are *not* one of the first five - the Dropout entry says freeze is a different
    /// fault and the Flip entry says a colour swap is - and neither had anywhere to be listed.
    ///
    /// Four more were considered and left out, because a kind here is a promise that a detector
    /// could be written against what the app measures:
    ///   - tearing / rolling: needs a spatial seam model, and the tile grid gives no handle on it.
    ///     The horizontal wipe these panels do is already turned away by MaxOnsetSpread.
    ///   - block artefacts: needs a texture model, and would fire on real content.
    ///   - geometry offset or rotation: the same measurement as Flip - a tile layout comparison -
    ///     so it belongs inside Flip rather than beside it.
    ///   - dead pixels and backlight mura: a panel test against a static target, not a runtime
    ///     anomaly, and it needs a different kind of session entirely.
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
        /// <summary>The picture stops being updated - the same frame keeps arriving.</summary>
        Freeze = 5,
        /// <summary>Colour is wrong: a band is missing, swapped, or badly out of balance.</summary>
        ColorShift = 6,
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
    /// settings window can list every kind before six of them exist.
    /// </summary>
    public static class AnomalyCatalog
    {
        /// <summary>Every kind, in the order the settings window shows them.</summary>
        public static readonly AnomalyKind[] All =
        {
            AnomalyKind.Dropout, AnomalyKind.Blackout, AnomalyKind.Washout,
            AnomalyKind.Flicker, AnomalyKind.Flip, AnomalyKind.Freeze,
            AnomalyKind.ColorShift,
        };

        /// <summary>Number of kinds, for arrays indexed by <see cref="AnomalyKind"/>.</summary>
        public const int Count = 7;

        /// <summary>
        /// Whether a detector exists. Only Dropout does, and it took a week of measurement to
        /// trust: a rate-based depth threshold, a coherence gate, an onset-spread gate, an event
        /// cap and a histogram that excludes the frames around an event.
        /// </summary>
        /// <summary>
        /// Whether anything in this build judges this kind.
        ///
        /// Two now. They do not share a state machine and could not: Dropout measures a fall
        /// relative to a running baseline and confirms on one frame; Blackout measures an
        /// absolute level with hysteresis and must not. `CONTEXT.md` makes that the dividing line
        /// between an event kind and a sustained one.
        ///
        /// When a learned inspector arrives this stops being a yes-or-no question - a kind will
        /// be implemented by the tiles, by the inspector, or by nothing yet. `docs/adr/0002`
        /// carries that shape; until then two kinds are true.
        /// </summary>
        public static bool Implemented(AnomalyKind kind) =>
            kind == AnomalyKind.Dropout || kind == AnomalyKind.Blackout;

        /// <summary>
        /// Whether the kind is judged by the sustained detector rather than the event one.
        ///
        /// The caller needs this because the two take different settings and emit on different
        /// occasions - one on close, one on confirm.
        /// </summary>
        public static bool IsSustained(AnomalyKind kind) => kind == AnomalyKind.Blackout;

        /// <summary>Which way this kind moves brightness.</summary>
        public static AnomalyDirection DirectionOf(AnomalyKind kind)
        {
            switch (kind)
            {
                case AnomalyKind.Dropout:
                case AnomalyKind.Blackout: return AnomalyDirection.Fall;
                case AnomalyKind.Washout: return AnomalyDirection.Rise;
                // Freeze does not move brightness at all - that is the whole symptom - and a
                // colour shift moves the bands against each other rather than the mean.
                default: return AnomalyDirection.Either;
            }
        }

        /// <summary>
        /// The word for a deviation of this kind, so a report reads correctly: a fall has a depth,
        /// a rise does not.
        /// </summary>
        public static string DeviationWord(AnomalyKind kind)
        {
            // Freeze reads the other way round: its number is how much change is still counted as
            // no change, so "delta" rather than a depth or a rise. See DeviationIsCeiling.
            if (kind == AnomalyKind.Freeze) return "delta";

            switch (DirectionOf(kind))
            {
                case AnomalyDirection.Fall: return "depth";
                case AnomalyDirection.Rise: return "rise";
                default: return "dev";
            }
        }

        /// <summary>
        /// Whether the stored deviation is a ceiling rather than a floor - that is, whether the
        /// kind is confirmed by the measurement being *below* it.
        ///
        /// True only for Freeze, and written down here rather than left to the detector that will
        /// one day compare them: every other kind fires when the move exceeds the threshold, so a
        /// Freeze detector written to the same shape would report a fault whenever the picture was
        /// alive. Sensor noise is what makes the test work at all - a live camera never sends two
        /// identical tile grids - and that also means the threshold is small and not zero.
        /// </summary>
        public static bool DeviationIsCeiling(AnomalyKind kind) => kind == AnomalyKind.Freeze;

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
                case AnomalyKind.Freeze: return "화면이 갱신을 멈춥니다 — 같은 그림이 계속 옵니다";
                case AnomalyKind.ColorShift: return "색이 틀어집니다 — 채널이 빠지거나 뒤바뀝니다";
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
