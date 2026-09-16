using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What counts as the picture being gone. Per camera, like every other measured value.
    ///
    /// **These are luma, 0 to 255, not a deviation.** That is the whole difference from
    /// <see cref="AnomalyThresholds"/>, whose Depth is a fraction of a running baseline. A
    /// sustained blackout cannot be measured against a running baseline at all - see
    /// <see cref="BlackoutDetector"/> - so the numbers here are absolute and have to come from
    /// what the panel actually reads, which is what <see cref="BlackoutDetector.Propose"/> is for.
    /// </summary>
    public sealed class BlackoutThresholds
    {
        /// <summary>
        /// Tile-median luma at or below which a frame is dark enough to be a candidate.
        ///
        /// Measured 2026-09-11 at the 120 fps operating point, the three working panels floored
        /// at **121.6 / 90.4 / 36.9** luma, so 6 clears the darkest of them by a factor of six.
        /// That is far more headroom than the earlier reading suggested - 17.2 / 13.6 / 7.8 on the
        /// unlit bench on 2026-09-10, which would have left under two luma - and the difference
        /// is the exposure and what the panels were showing.
        ///
        /// **Both sides are now measured, and the margin on the catch side is thin.** An exposure
        /// sweep on 2026-09-12 (8289 / 2000 / 500 / 150 us) showed the curve is not proportional -
        /// it has a pedestal. Extrapolated to zero light the three channels read **4.94 / 5.36 /
        /// 5.07** luma, which is what a covered lens would give. So 6.0 is on the right side of a
        /// real blackout, but by **0.64 luma on the worst channel**, against a measurement spread
        /// of 0.75 at the lowest exposure. Sensor noise is the same size as the margin.
        ///
        /// That sweep also produced the **first unforced detection on hardware**: at 150 us
        /// Camera 2 read 5.1 luma with a spread of 0.3, and the detector confirmed and held -
        /// no thresholds moved to make it happen. The other two read 7.3 and 6.6, flat enough but
        /// not dark enough, and correctly did not fire.
        ///
        /// **Open decision.** The proposal suggests 10, which would sit 4.6 luma above the
        /// pedestal - six times the noise - and still 28% of the darkest working panel. Left at 6
        /// until somebody covers a lens and measures it directly, since an extrapolation is not a
        /// covered camera.
        /// </summary>
        public double EnterLuma { get; set; } = 6.0;

        /// <summary>
        /// Luma the frame has to reach to be called back. Above <see cref="EnterLuma"/> on
        /// purpose: that gap is the hysteresis, and without it a panel sitting on the threshold
        /// would report a fault every few frames. CONTEXT.md makes hysteresis the thing that
        /// separates a sustained kind from an event one.
        /// </summary>
        public double ExitLuma { get; set; } = 12.0;

        /// <summary>
        /// How far apart the tiles may be and still count as flat: max minus min of the populated
        /// tile means, in luma.
        ///
        /// **This is what separates a dead panel from a black picture**, and it is the part of
        /// this detector that does not depend on an absolute level being calibrated. A dark
        /// picture is still a picture - a night-mode map, a letterboxed frame, a fade - and its
        /// tiles differ. A panel that is off has only sensor noise, so its tiles agree.
        /// </summary>
        public double MaxSpread { get; set; } = 4.0;

        /// <summary>
        /// How long dark-and-flat has to hold before it is a fault, in milliseconds.
        ///
        /// Not one frame. Dropout is the kind that must confirm on a single frame; this is the
        /// kind that must not, because a blackout is defined by staying gone and because a single
        /// flat dark frame is what a legitimate cut to black looks like.
        /// </summary>
        public double EnterMs { get; set; } = 500.0;

        /// <summary>How long the picture has to be back before the fault is over.</summary>
        public double RecoverMs { get; set; } = 500.0;

        public void CopyFrom(BlackoutThresholds other)
        {
            if (other == null) return;
            EnterLuma = Clamp(other.EnterLuma, 0.0, 254.0);
            ExitLuma = Clamp(other.ExitLuma, 0.0, 255.0);
            MaxSpread = Clamp(other.MaxSpread, 0.0, 255.0);
            EnterMs = Clamp(other.EnterMs, 0.0, 600000.0);
            RecoverMs = Clamp(other.RecoverMs, 0.0, 600000.0);
            Normalize();
        }

        /// <summary>
        /// Makes the pair a hysteresis rather than a single line. An exit at or below the entry
        /// is not a configuration, it is a chatter generator, so it is lifted rather than obeyed.
        /// </summary>
        public void Normalize()
        {
            if (ExitLuma <= EnterLuma) ExitLuma = EnterLuma + 1.0;
        }

        /// <summary>Frames the entry dwell comes to at this rate. At least one.</summary>
        public int EnterFrames(double fps) => Frames(EnterMs, fps);

        /// <summary>Frames the recovery dwell comes to at this rate. At least one.</summary>
        public int RecoverFrames(double fps) => Frames(RecoverMs, fps);

        private static int Frames(double ms, double fps)
        {
            double f = ms * fps / 1000.0;
            if (double.IsNaN(f) || f < 1.0) return 1;
            return f > int.MaxValue ? int.MaxValue : (int)Math.Round(f);
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }

    /// <summary>
    /// What a quiet run says the blackout thresholds could be.
    ///
    /// The same shape as <see cref="DepthProposal"/> and for the same reason: a threshold picked
    /// by hand is a threshold nobody can defend. This one reports the floor the panel actually
    /// reached while it was working, so a level set below it cannot fire on a healthy screen.
    /// </summary>
    public readonly struct BlackoutProposal
    {
        /// <summary>
        /// How near zero a dead panel is taken to read, in luma.
        ///
        /// Not measured - nothing here has ever seen a panel that was off. It is the ceiling a
        /// proposal will not exceed, so the suggestion stays a statement about the sensor rather
        /// than about how bright this particular picture happened to be.
        /// </summary>
        public const double NearSensorFloor = 10.0;

        public BlackoutProposal(double minLuma, double maxFlatSpreadAtDarkest,
                                long framesJudged, double suggestedEnterLuma,
                                double suggestedMaxSpread)
        {
            MinLuma = minLuma;
            MaxFlatSpreadAtDarkest = maxFlatSpreadAtDarkest;
            FramesJudged = framesJudged;
            SuggestedEnterLuma = suggestedEnterLuma;
            SuggestedMaxSpread = suggestedMaxSpread;
        }

        /// <summary>The darkest the working panel got. A threshold at or above this would fire on it.</summary>
        public double MinLuma { get; }

        /// <summary>Tile spread on the darkest frame seen. Says how much structure a dark picture keeps.</summary>
        public double MaxFlatSpreadAtDarkest { get; }

        public long FramesJudged { get; }

        /// <summary>
        /// A level the measurement can defend, which is not half the floor.
        ///
        /// Halving is right for a depth, which is a fraction of a signal, and wrong for this,
        /// which is an absolute level whose target reads near zero. The 2026-09-11 calibration
        /// hour floored at 118.9 / 87.8 / 36.2 luma, so halving proposed 59.5 / 43.9 / 18.1 -
        /// and **59 luma is not a panel that is gone, it is a panel at half brightness**, which
        /// is what Dropout is for. The flatness gate would have caught most of that, but leaning
        /// on it alone is thin.
        ///
        /// So a quarter of the floor, capped at <see cref="NearSensorFloor"/>. The cap carries
        /// the meaning: a panel that is off reads near the sensor floor however bright the
        /// working picture was, so a brighter channel does not earn a higher threshold.
        ///
        /// **This bounds false positives and nothing else.** What it takes to catch a real
        /// blackout cannot come from an hour in which nothing went black - that needs a covered
        /// camera.
        /// </summary>
        public double SuggestedEnterLuma { get; }

        /// <summary>A quarter of the structure the darkest healthy frame kept, capped the same way.</summary>
        public double SuggestedMaxSpread { get; }

        /// <summary>
        /// Whether there is anything to propose from. An hour at 120 fps is 432,000 frames; the
        /// bar here is the same 10,000 the depth proposal uses, which is 83 seconds.
        /// </summary>
        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames
                             && MinLuma > 0.0 && SuggestedEnterLuma > 0.0;

        /// <summary>
        /// Whether the run ever saw a frame that was not already dark.
        ///
        /// False means the panel was gone for the whole run - or the threshold is above what a
        /// working panel reads, which is how a forced test looks. Either way there is no floor to
        /// report, and printing 0.0 would read as "the panel floors at zero" rather than "nothing
        /// was measured". Found in the log of the first end-to-end test, 2026-09-11.
        /// </summary>
        public bool SawHealthyFrame => MinLuma > 0.0;

        public override string ToString() =>
            !SawHealthyFrame
                ? $"blackout proposal - no frame was outside a blackout, so there is no floor to "
                + $"propose from ({FramesJudged} frames judged). Either the panel was gone for the "
                + $"whole run, or the entry level is above what a working panel reads."
                : $"blackout proposal - enter {SuggestedEnterLuma:F1} luma, spread {SuggestedMaxSpread:F1} "
                + $"(panel floor {MinLuma:F1} luma, spread there {MaxFlatSpreadAtDarkest:F1}, "
                + $"{FramesJudged} frames judged)"
                + (IsUsable ? string.Empty : " - NOT ENOUGH TO PROPOSE FROM");
    }

    /// <summary>
    /// Finds a picture that has gone and stayed gone.
    ///
    /// **It has its own state machine because it cannot share Dropout's.** Dropout measures
    /// `1 - median/baseline`, a fall relative to a running median, and `AnomalyDetector` freezes
    /// that baseline while an event is open with the comment "a fault must not become the new
    /// normal ... a long enough blackout would erase itself". It then has to cap the event at
    /// 2.01 s and adopt the dark level as normal, or the channel goes silent with an event open
    /// forever. So on a panel that dies and stays dead, the relative detector reports one
    /// truncated Dropout and then measures a deviation of zero against a black baseline. Measured
    /// 2026-09-10: a sustained occlusion arrives as a run of truncated Dropouts, which is
    /// `ClipScheduler`'s merge window papering over a kind that has no detector.
    ///
    /// This one holds no baseline, so there is nothing for the fault to erase. Two ideas do the
    /// work:
    ///
    /// **Dark and flat, not just dark.** A dark picture is still a picture and its tiles differ;
    /// a panel that is off has only sensor noise and its tiles agree. Flatness is the half of the
    /// test that needs no calibrated absolute level, which matters because the level cannot be
    /// calibrated until the optical setup lands.
    ///
    /// **Hysteresis both ways.** Entry and exit are different luma levels with a dwell on each
    /// side. CONTEXT.md makes that the defining property of a sustained kind, and it is what
    /// keeps a panel sitting near the line from reporting a fault every few frames.
    ///
    /// **It reports once.** The event is emitted when the dwell confirms, which is also when the
    /// evidence ring buffer still holds the transition - the diagnostic moment is the picture
    /// going, not the hours of black afterwards. <see cref="InBlackout"/> carries the ongoing
    /// state for health and the timeline, and recovery emits nothing. That follows the rule this
    /// repo already settled on for sustained faults: one report, not one every two seconds.
    /// </summary>
    public sealed class BlackoutDetector
    {
        private readonly BlackoutThresholds _t;
        private readonly SustainedFaultTracker _run = new SustainedFaultTracker(AnomalyKind.Blackout);

        private long _judged;
        private double _healthyFloor = double.MaxValue;
        private double _spreadAtFloor;

        public BlackoutDetector(BlackoutThresholds thresholds)
        {
            _t = thresholds ?? new BlackoutThresholds();
            _t.Normalize();
        }

        /// <summary>Luma of the last frame judged, for the tick to show.</summary>
        public double LastLuma { get; private set; }

        /// <summary>Tile spread of the last frame judged.</summary>
        public double LastSpread { get; private set; }

        /// <summary>Whether the picture is currently gone. What health and the timeline read.</summary>
        public bool InBlackout => _run.InFault;

        /// <summary>Frames the current blackout has run, or 0.</summary>
        public int RunningFrames => _run.RunningFrames;

        /// <summary>How long the current blackout has run, in ms, or 0.</summary>
        public double RunningMs => _run.RunningMs;

        /// <summary>Frames judged, which is what the proposal needs enough of.</summary>
        public long Judged => _judged;

        /// <summary>
        /// Whether a frame period has been measured yet, which takes two consecutive frames.
        ///
        /// Nothing is confirmed before it is true. A dwell in milliseconds is not checkable
        /// against a rate of zero, and the safe answer at the start of a grab is to judge nothing
        /// rather than to judge with a dwell of one frame.
        /// </summary>
        public bool RateKnown => _run.RateKnown;

        /// <summary>Frames skipped because the numbering jumped. The dwell restarts across a hole.</summary>
        public long FramesSkippedForGaps => _run.FramesSkippedForGaps;

        /// <summary>True on the frame the fault was confirmed, so stills can be kept.</summary>
        public bool EnteredThisFrame => _run.EnteredThisFrame;

        /// <summary>True on the first frame the picture is back.</summary>
        public bool RecoveredThisFrame => _run.RecoveredThisFrame;

        /// <summary>
        /// Judges one frame. Returns the event on the frame the dwell confirms it, and null every
        /// other frame - including every frame of a blackout already reported.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return null;

            double luma = grid.TileMedian();
            double spread = Spread(grid);
            LastLuma = luma;
            LastSpread = spread;
            _judged++;

            bool dark = luma <= _t.EnterLuma && spread <= _t.MaxSpread;
            bool back = luma >= _t.ExitLuma;

            // The floor a threshold has to clear, taken only from frames that are not a fault -
            // the same discipline the depth floor uses.
            if (!_run.InFault && !dark && luma < _healthyFloor)
            {
                _healthyFloor = luma;
                _spreadAtFloor = spread;
            }

            // How far below the entry level it went, as a fraction of that level, so the report
            // carries a number of the same shape Dropout's depth has.
            double below = _t.EnterLuma > 0.0 ? (_t.EnterLuma - luma) / _t.EnterLuma : 0.0;
            if (below < 0.0) below = 0.0;
            if (below > 1.0) below = 1.0;

            return _run.Observe(grid.FrameNumber, timeStampSec, dark, back,
                                below, _t.EnterMs, _t.RecoverMs);
        }

        /// <summary>
        /// Reports a blackout still running when acquisition stops, if it was never confirmed.
        ///
        /// A panel that died four frames before the run ended would otherwise be lost entirely -
        /// the dwell never completed and nothing was emitted.
        /// </summary>
        public AnomalyEvent? Flush() => _run.Flush();

        /// <summary>
        /// What a run says the thresholds could be.
        ///
        /// Halving rather than fitting a distribution: the quantity is a hard floor, not a noise
        /// population, so the question is margin and not probability.
        /// </summary>
        public BlackoutProposal Propose()
        {
            double floor = _healthyFloor == double.MaxValue ? 0.0 : _healthyFloor;
            double spread = _healthyFloor == double.MaxValue ? 0.0 : _spreadAtFloor;
            return new BlackoutProposal(floor, spread, _judged, Bound(floor), Bound(spread));
        }

        /// <summary>
        /// A quarter of what was measured, and never above the sensor floor.
        ///
        /// The quarter keeps it clear of the working level; the cap keeps a bright channel from
        /// being handed a high threshold just because its picture is bright.
        /// </summary>
        private static double Bound(double measured) =>
            measured <= 0.0 ? 0.0 : Math.Min(measured / 4.0, BlackoutProposal.NearSensorFloor);

        public void Reset()
        {
            _run.Reset();
            _healthyFloor = double.MaxValue;
            _spreadAtFloor = 0.0;
            _judged = 0;
            LastLuma = 0.0;
            LastSpread = 0.0;
        }

        /// <summary>
        /// Max minus min of the populated tile means.        /// <summary>
        /// Max minus min of the populated tile means.
        ///
        /// Means and not standard deviations: what tells a dead panel from a dark picture is that
        /// one part of the screen differs from another, which is a statement about tiles and not
        /// about pixels inside a tile. A tile of uniform grey and a tile of noise have very
        /// different stdevs and the same mean, and it is the mean that carries the picture.
        /// </summary>
        public static double Spread(TileGrid grid)
        {
            if (grid == null) return 0.0;
            double lo = double.MaxValue, hi = double.MinValue;
            int seen = 0;
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                if (!grid.IsPopulated(i)) continue;
                double m = grid.Mean(i);
                if (m < lo) lo = m;
                if (m > hi) hi = m;
                seen++;
            }
            return seen < 2 ? 0.0 : hi - lo;
        }
    }
}
