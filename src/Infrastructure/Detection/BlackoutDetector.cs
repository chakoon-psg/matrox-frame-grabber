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
        /// Provisional. Measured 2026-09-10 on the unlit bench, the three channels' ROI luma read
        /// 17.2 / 13.6 / 7.8 with the panels alive and showing a dark screen, so a threshold that
        /// clears the darkest of them has under two luma of margin. **The optical setup and the
        /// quiet hour have to land before this number means anything** - until then the flatness
        /// gate is doing most of the work.
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

        /// <summary>Half the observed floor: a doubling of margin, which is the rest of this repo's habit.</summary>
        public double SuggestedEnterLuma { get; }

        /// <summary>Half the spread the darkest healthy frame kept.</summary>
        public double SuggestedMaxSpread { get; }

        /// <summary>
        /// Whether there is anything to propose from. An hour at 120 fps is 432,000 frames; the
        /// bar here is the same 10,000 the depth proposal uses, which is 83 seconds.
        /// </summary>
        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames
                             && MinLuma > 0.0 && SuggestedEnterLuma > 0.0;

        public override string ToString() =>
            $"blackout proposal - enter {SuggestedEnterLuma:F1} luma, spread {SuggestedMaxSpread:F1} "
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

        private int _darkRun;          // consecutive dark-and-flat frames
        private int _clearRun;         // consecutive frames back above ExitLuma
        private bool _inBlackout;
        private bool _reported;

        private long _startFrame, _lastFrame;
        private long _previousFrame = -1;
        private double _startTime, _lastTime, _previousTime;
        private double _framePeriodSec;
        private double _darkestLuma = double.MaxValue;
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
        public bool InBlackout => _inBlackout;

        /// <summary>Frames the current blackout has run, or 0.</summary>
        public int RunningFrames => _inBlackout ? (int)(_lastFrame - _startFrame + 1) : 0;

        /// <summary>How long the current blackout has run, in ms, or 0.</summary>
        public double RunningMs => _inBlackout ? RunningFrames * _framePeriodSec * 1000.0 : 0.0;

        /// <summary>Frames judged, which is what the proposal needs enough of.</summary>
        public long Judged => _judged;

        /// <summary>Frames skipped because the numbering jumped. The dwell restarts across a hole.</summary>
        public long FramesSkippedForGaps { get; private set; }

        /// <summary>True on the frame the fault was confirmed, so stills can be kept.</summary>
        public bool EnteredThisFrame { get; private set; }

        /// <summary>True on the first frame the picture is back.</summary>
        public bool RecoveredThisFrame { get; private set; }

        /// <summary>
        /// Judges one frame. Returns the event on the frame the dwell confirms it, and null every
        /// other frame - including every frame of a blackout already reported.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return null;

            EnteredThisFrame = false;
            RecoveredThisFrame = false;

            long frame = grid.FrameNumber;

            // A hole in the numbering is not a measurement. The level would still be readable
            // across it - this detector needs no delta - but the dwell is a claim about how long
            // something held, and frames nobody saw cannot support it. So the run restarts.
            if (_previousFrame >= 0 && frame != _previousFrame + 1)
            {
                FramesSkippedForGaps++;
                _darkRun = 0;
                _clearRun = 0;
                _previousFrame = frame;
                _previousTime = timeStampSec;
                return null;
            }

            if (_previousFrame >= 0 && timeStampSec > _previousTime)
                _framePeriodSec = timeStampSec - _previousTime;
            _previousFrame = frame;
            _previousTime = timeStampSec;

            double luma = grid.TileMedian();
            double spread = Spread(grid);
            LastLuma = luma;
            LastSpread = spread;
            _judged++;

            if (luma < _darkestLuma) _darkestLuma = luma;

            bool dark = luma <= _t.EnterLuma && spread <= _t.MaxSpread;
            bool back = luma >= _t.ExitLuma;

            // The floor a threshold has to clear, taken only from frames that are not a fault -
            // the same discipline the depth floor uses.
            if (!_inBlackout && !dark && luma < _healthyFloor)
            {
                _healthyFloor = luma;
                _spreadAtFloor = spread;
            }

            AnomalyEvent? emitted = null;

            if (dark)
            {
                _clearRun = 0;
                if (_darkRun == 0) { _startFrame = frame; _startTime = timeStampSec; }
                _darkRun++;
                _lastFrame = frame;
                _lastTime = timeStampSec;

                if (!_reported && _darkRun >= _t.EnterFrames(Fps()))
                {
                    _inBlackout = true;
                    _reported = true;
                    EnteredThisFrame = true;
                    emitted = Emit();
                }
                else if (_inBlackout)
                {
                    _lastFrame = frame;
                }
            }
            else
            {
                _darkRun = 0;
                if (_inBlackout) _lastFrame = frame;

                if (back)
                {
                    _clearRun++;
                    if (_clearRun >= _t.RecoverFrames(Fps()))
                    {
                        if (_inBlackout) RecoveredThisFrame = true;
                        _inBlackout = false;
                        _reported = false;
                        _clearRun = 0;
                    }
                }
                else
                {
                    // Between the two levels: neither dark enough to start nor bright enough to
                    // end. That gap is the hysteresis, and sitting in it changes nothing.
                    _clearRun = 0;
                }
            }

            return emitted;
        }

        /// <summary>
        /// Reports a blackout still running when acquisition stops, if it was never confirmed.
        ///
        /// A panel that died four frames before the run ended would otherwise be lost entirely -
        /// the dwell never completed and nothing was emitted.
        /// </summary>
        public AnomalyEvent? Flush()
        {
            if (_reported || _darkRun < 1) return null;
            _inBlackout = true;
            _reported = true;
            return Emit(truncated: true);
        }

        /// <summary>
        /// What a run says the thresholds could be. Half the observed floor, in both axes.
        ///
        /// Halving rather than fitting a distribution: the quantity is a hard floor, not a noise
        /// population, so the question is margin and not probability. It is the same doubling of
        /// margin the depth threshold was set with.
        /// </summary>
        public BlackoutProposal Propose()
        {
            double floor = _healthyFloor == double.MaxValue ? 0.0 : _healthyFloor;
            double spread = _healthyFloor == double.MaxValue ? 0.0 : _spreadAtFloor;
            return new BlackoutProposal(floor, spread, _judged, floor / 2.0, spread / 2.0);
        }

        public void Reset()
        {
            _darkRun = 0;
            _clearRun = 0;
            _inBlackout = false;
            _reported = false;
            _previousFrame = -1;
            _previousTime = 0.0;
            _framePeriodSec = 0.0;
            _darkestLuma = double.MaxValue;
            _healthyFloor = double.MaxValue;
            _spreadAtFloor = 0.0;
            _judged = 0;
            FramesSkippedForGaps = 0;
            LastLuma = 0.0;
            LastSpread = 0.0;
            EnteredThisFrame = false;
            RecoveredThisFrame = false;
        }

        /// <summary>
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

        private double Fps() => _framePeriodSec > 0.0 ? 1.0 / _framePeriodSec : 0.0;

        private AnomalyEvent Emit(bool truncated = false)
        {
            int frames = (int)(_lastFrame - _startFrame + 1);
            double period = _framePeriodSec > 0.0 ? _framePeriodSec : 0.0;

            // MaxDeviation carries how far below the entry level it went, as a fraction of that
            // level, so the report has a number of the same shape Dropout's depth has. Coherence
            // is not measured here - this detector never looks at a delta - and saying 0 would
            // read as "the tiles disagreed". 1.0 is the truth: every tile agrees, which is the
            // whole finding.
            double below = _t.EnterLuma > 0.0
                ? (_t.EnterLuma - _darkestLuma) / _t.EnterLuma
                : 0.0;
            if (below < 0.0) below = 0.0;
            if (below > 1.0) below = 1.0;

            return new AnomalyEvent(
                _startFrame, _lastFrame, frames,
                below, 1.0,
                _startTime, frames * period * 1000.0,
                truncated,
                onsetSpreadFrames: 0, onsetTiles: 0,
                kind: AnomalyKind.Blackout);
        }
    }
}
