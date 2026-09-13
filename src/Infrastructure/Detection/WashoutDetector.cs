using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What counts as the picture being washed out. Per camera, in luma.
    ///
    /// **The levels are the weaker half of this test.** `CONTEXT.md` is explicit that Washout is
    /// "밝은데 구조가 없는" - bright with no structure - and that the judgement is the collapse of
    /// structure rather than the brightness, because a legitimately bright screen cannot be told
    /// from a washed-out one by mean brightness. So <see cref="MaxSpread"/> and
    /// <see cref="MaxDetail"/> carry the decision and the luma levels only say where to start
    /// looking.
    /// </summary>
    public sealed class WashoutThresholds
    {
        /// <summary>
        /// Tile-median luma at or above which a frame is bright enough to be a candidate.
        ///
        /// Provisional. The working panels read 116 / 85 / 35 luma at the operating point, so a
        /// level near 200 is far above anything measured here - but a white UI background would
        /// also reach it, which is exactly why the structure tests below decide and this does not.
        /// </summary>
        public double EnterLuma { get; set; } = 200.0;

        /// <summary>
        /// Luma the frame has to fall back to before the fault is over. Below
        /// <see cref="EnterLuma"/>: that gap is the hysteresis.
        /// </summary>
        public double ExitLuma { get; set; } = 180.0;

        /// <summary>
        /// How far apart the tile means may be and still count as structureless, in luma.
        ///
        /// The same measure Blackout uses, for the same reason: one part of the screen differing
        /// from another is what a picture is.
        /// </summary>
        public double MaxSpread { get; set; } = 4.0;

        /// <summary>
        /// How much detail may remain *inside* a tile, as the mean of the per-tile standard
        /// deviations.
        ///
        /// **This is the test Blackout does not need and Washout cannot do without.** A white UI
        /// background has flat tile means across a large region but carries text and icons, so
        /// the variance inside a tile stays high. A saturated panel has neither. Without this a
        /// white settings screen would be reported as a fault.
        ///
        /// `TileGrid.Stdev` has been there since the tile work, and its comment already said this
        /// value would travel into "the washout decision".
        /// </summary>
        public double MaxDetail { get; set; } = 3.0;

        /// <summary>How long bright-and-structureless has to hold before it is a fault, in ms.</summary>
        public double EnterMs { get; set; } = 500.0;

        /// <summary>How long the picture has to be back before the fault is over, in ms.</summary>
        public double RecoverMs { get; set; } = 500.0;

        public void CopyFrom(WashoutThresholds other)
        {
            if (other == null) return;
            EnterLuma = Clamp(other.EnterLuma, 1.0, 255.0);
            ExitLuma = Clamp(other.ExitLuma, 0.0, 254.0);
            MaxSpread = Clamp(other.MaxSpread, 0.0, 255.0);
            MaxDetail = Clamp(other.MaxDetail, 0.0, 255.0);
            EnterMs = Clamp(other.EnterMs, 1.0, 600000.0);
            RecoverMs = Clamp(other.RecoverMs, 1.0, 600000.0);
            Normalize();
        }

        /// <summary>
        /// Makes the pair a hysteresis. Here the exit is *below* the entry - the mirror of
        /// Blackout - and an exit at or above it is a chatter generator rather than a setting.
        /// </summary>
        public void Normalize()
        {
            if (ExitLuma >= EnterLuma) ExitLuma = EnterLuma - 1.0;
            if (ExitLuma < 0.0) ExitLuma = 0.0;
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }

    /// <summary>What a run says the washout thresholds could be.</summary>
    public readonly struct WashoutProposal
    {
        public WashoutProposal(double maxLuma, double detailAtBrightest, long framesJudged,
                               double suggestedEnterLuma, double suggestedMaxDetail)
        {
            MaxLuma = maxLuma;
            DetailAtBrightest = detailAtBrightest;
            FramesJudged = framesJudged;
            SuggestedEnterLuma = suggestedEnterLuma;
            SuggestedMaxDetail = suggestedMaxDetail;
        }

        /// <summary>The brightest the working panel got. A level below this would fire on it.</summary>
        public double MaxLuma { get; }

        /// <summary>Detail the brightest healthy frame still carried. A real washout has none.</summary>
        public double DetailAtBrightest { get; }

        public long FramesJudged { get; }

        /// <summary>Halfway from the brightest healthy frame to saturation, capped below 255.</summary>
        public double SuggestedEnterLuma { get; }

        /// <summary>A quarter of the detail the brightest healthy frame kept.</summary>
        public double SuggestedMaxDetail { get; }

        /// <summary>Whether the run ever saw a frame that was not already washed out.</summary>
        public bool SawHealthyFrame => MaxLuma > 0.0;

        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames
                             && SawHealthyFrame && SuggestedEnterLuma > 0.0;

        public override string ToString() =>
            !SawHealthyFrame
                ? $"washout proposal - no frame was outside a washout, so there is no ceiling to "
                + $"propose from ({FramesJudged} frames judged)"
                : $"washout proposal - enter {SuggestedEnterLuma:F1} luma, detail {SuggestedMaxDetail:F1} "
                + $"(panel ceiling {MaxLuma:F1} luma, detail there {DetailAtBrightest:F1}, "
                + $"{FramesJudged} frames judged)"
                + (IsUsable ? string.Empty : " - NOT ENOUGH TO PROPOSE FROM");
    }

    /// <summary>
    /// Finds a picture that has gone bright and lost its structure.
    ///
    /// **Not the mirror of Blackout, although it looks like one.** Blackout leads with the level
    /// and uses flatness to tell a dead panel from a dark picture. Washout cannot lead with the
    /// level at all: `CONTEXT.md` says so directly - a white UI background and a saturated panel
    /// have the same mean brightness, and only the structure tells them apart. So the level is a
    /// gate on where to look and the two structure measures make the decision.
    ///
    /// Two structure measures rather than one, and the second is the whole difference:
    ///
    /// **Between tiles** (<see cref="WashoutThresholds.MaxSpread"/>) - the same range of tile
    /// means Blackout uses. A large white region passes this, which is why it is not enough.
    ///
    /// **Inside tiles** (<see cref="WashoutThresholds.MaxDetail"/>) - the mean of the per-tile
    /// standard deviations. A white settings screen carries text and icons, so the variance
    /// inside a tile stays high even where the tile means agree. A saturated panel has neither.
    ///
    /// The state machine is shared with Blackout (<see cref="SustainedFaultTracker"/>): dwell on
    /// both sides, hysteresis, one report, no cap. What differs is only the question asked of each
    /// frame.
    ///
    /// **Nothing here has seen a real washout.** The thresholds bound false positives against the
    /// panels as they are, and what it takes to catch a saturated panel is not derivable from a
    /// run in which nothing saturated - the same limit Blackout has.
    /// </summary>
    public sealed class WashoutDetector
    {
        private readonly WashoutThresholds _t;
        private readonly SustainedFaultTracker _run = new SustainedFaultTracker(AnomalyKind.Washout);

        private long _judged;
        private double _healthyCeiling = double.MinValue;
        private double _detailAtCeiling;

        public WashoutDetector(WashoutThresholds thresholds)
        {
            _t = thresholds ?? new WashoutThresholds();
            _t.Normalize();
        }

        /// <summary>Tile-median luma of the last frame judged.</summary>
        public double LastLuma { get; private set; }

        /// <summary>Range of the tile means on the last frame.</summary>
        public double LastSpread { get; private set; }

        /// <summary>Mean per-tile standard deviation on the last frame - the detail that remains.</summary>
        public double LastDetail { get; private set; }

        public bool InWashout => _run.InFault;
        public int RunningFrames => _run.RunningFrames;
        public double RunningMs => _run.RunningMs;
        public long Judged => _judged;
        public bool RateKnown => _run.RateKnown;
        public long FramesSkippedForGaps => _run.FramesSkippedForGaps;
        public bool EnteredThisFrame => _run.EnteredThisFrame;
        public bool RecoveredThisFrame => _run.RecoveredThisFrame;

        /// <summary>
        /// Judges one frame. Returns the event on the frame the dwell confirms it, and null on
        /// every other frame.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return null;

            double luma = grid.TileMedian();
            double spread = BlackoutDetector.Spread(grid);
            double detail = Detail(grid);
            LastLuma = luma;
            LastSpread = spread;
            LastDetail = detail;
            _judged++;

            bool washed = luma >= _t.EnterLuma
                       && spread <= _t.MaxSpread
                       && detail <= _t.MaxDetail;
            bool back = luma <= _t.ExitLuma;

            // The ceiling a level has to clear, from frames that are not a fault.
            if (!_run.InFault && !washed && luma > _healthyCeiling)
            {
                _healthyCeiling = luma;
                _detailAtCeiling = detail;
            }

            // How far past the entry level it went, as a fraction of the remaining headroom to
            // saturation, so the number has the same shape as a depth.
            double head = 255.0 - _t.EnterLuma;
            double over = head > 0.0 ? (luma - _t.EnterLuma) / head : 0.0;
            if (over < 0.0) over = 0.0;
            if (over > 1.0) over = 1.0;

            return _run.Observe(grid.FrameNumber, timeStampSec, washed, back,
                                over, _t.EnterMs, _t.RecoverMs);
        }

        /// <summary>Reports a washout still running but unconfirmed when acquisition stops.</summary>
        public AnomalyEvent? Flush() => _run.Flush();

        public WashoutProposal Propose()
        {
            bool saw = _healthyCeiling != double.MinValue;
            double ceiling = saw ? _healthyCeiling : 0.0;
            double detail = saw ? _detailAtCeiling : 0.0;

            // Halfway from the brightest healthy frame to saturation: clear of what the panel
            // does, and still reachable by something that has genuinely saturated. Never 255,
            // because a level nothing can reach is a detector that is switched off.
            double enter = saw ? Math.Min(254.0, ceiling + (255.0 - ceiling) / 2.0) : 0.0;
            return new WashoutProposal(ceiling, detail, _judged, enter, detail / 4.0);
        }

        public void Reset()
        {
            _run.Reset();
            _healthyCeiling = double.MinValue;
            _detailAtCeiling = 0.0;
            _judged = 0;
            LastLuma = 0.0;
            LastSpread = 0.0;
            LastDetail = 0.0;
        }

        /// <summary>
        /// Mean of the per-tile standard deviations: how much detail the picture still carries
        /// inside its tiles.
        ///
        /// The mean rather than the max, because one tile keeping an edge should not excuse a
        /// screen that has otherwise gone blank - and the max is what a single bright reflection
        /// would dominate.
        /// </summary>
        public static double Detail(TileGrid grid)
        {
            if (grid == null) return 0.0;
            double sum = 0.0;
            int seen = 0;
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                if (!grid.IsPopulated(i)) continue;
                sum += grid.Stdev(i);
                seen++;
            }
            return seen < 1 ? 0.0 : sum / seen;
        }
    }
}
