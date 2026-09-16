using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What counts as the picture having stopped. Per camera, in luma of change per frame.
    ///
    /// **These are the only thresholds in the project that confirm by being under-run.** Every
    /// other kind fires when a number exceeds a level; this one fires when the change between
    /// consecutive frames falls below one. `CONTEXT.md` calls it out as the single inverted kind.
    /// </summary>
    public sealed class FreezeThresholds
    {
        /// <summary>
        /// Mean absolute change in tile means, per frame, at or below which the picture counts as
        /// stopped.
        ///
        /// **Not zero, and that is the whole idea.** A live camera never sends the same tile grid
        /// twice: sensor noise guarantees a difference even on a perfectly static scene. So the
        /// threshold sits below the noise a working rig produces, and anything under it is not a
        /// still picture - it is the same picture arriving again.
        ///
        /// Provisional until measured. <see cref="FreezeDetector.Propose"/> reports the quietest
        /// change a run actually saw, which is that noise floor.
        /// </summary>
        public double EnterDelta { get; set; } = 0.02;

        /// <summary>
        /// Change the picture has to reach again before the fault is over. Above
        /// <see cref="EnterDelta"/>: that gap is the hysteresis.
        /// </summary>
        public double ExitDelta { get; set; } = 0.06;

        /// <summary>
        /// How long the picture has to be unchanging before it is a fault, in ms.
        ///
        /// Longer than the other sustained kinds. A dropped frame or two of genuinely identical
        /// content is not a frozen panel, and a second of it is the shortest thing worth waking
        /// somebody for.
        /// </summary>
        public double EnterMs { get; set; } = 1000.0;

        /// <summary>How long it has to be moving again before the fault is over, in ms.</summary>
        public double RecoverMs { get; set; } = 500.0;

        public void CopyFrom(FreezeThresholds other)
        {
            if (other == null) return;
            EnterDelta = Clamp(other.EnterDelta, 0.0, 255.0);
            ExitDelta = Clamp(other.ExitDelta, 0.0, 255.0);
            EnterMs = Clamp(other.EnterMs, 1.0, 600000.0);
            RecoverMs = Clamp(other.RecoverMs, 1.0, 600000.0);
            Normalize();
        }

        /// <summary>
        /// Keeps the pair a hysteresis. An exit at or below the entry would let a rig sitting on
        /// the line report a fault every few frames.
        /// </summary>
        public void Normalize()
        {
            if (ExitDelta <= EnterDelta) ExitDelta = EnterDelta * 2.0 + 1e-6;
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }

    /// <summary>What a run says the freeze threshold could be.</summary>
    public readonly struct FreezeProposal
    {
        public FreezeProposal(double quietestChange, double medianChange, long framesJudged,
                              double suggestedEnterDelta)
        {
            QuietestChange = quietestChange;
            MedianChange = medianChange;
            FramesJudged = framesJudged;
            SuggestedEnterDelta = suggestedEnterDelta;
        }

        /// <summary>
        /// The smallest frame-to-frame change the run saw while the picture was live.
        ///
        /// This is the sensor noise floor, and it is the number the whole kind rests on: a
        /// threshold above it fires on a static screen, and a threshold far below it never fires
        /// at all.
        /// </summary>
        public double QuietestChange { get; }

        /// <summary>Typical change, for scale. A moving scene is orders above a static one.</summary>
        public double MedianChange { get; }

        public long FramesJudged { get; }

        /// <summary>Half the quietest change seen: under the noise, still reachable by a repeat.</summary>
        public double SuggestedEnterDelta { get; }

        public bool SawLiveFrames => QuietestChange > 0.0;

        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames
                             && SawLiveFrames;

        public override string ToString() =>
            !SawLiveFrames
                ? $"freeze proposal - the quietest change measured was zero, which means either a "
                + $"frozen source or too few frames ({FramesJudged} judged)"
                : $"freeze proposal - enter {SuggestedEnterDelta:F4} "
                + $"(noise floor {QuietestChange:F4}, typical {MedianChange:F4} luma/frame, "
                + $"{FramesJudged} frames judged)"
                + (IsUsable ? string.Empty : " - NOT ENOUGH TO PROPOSE FROM");
    }

    /// <summary>
    /// Finds a picture that has stopped being updated.
    ///
    /// **The one kind whose threshold is a floor rather than a ceiling.** Brightness stays in
    /// range while a panel is frozen, so no deviation threshold can see it; what changes is that
    /// the picture stops changing. `CONTEXT.md` names this as the single inverted kind.
    ///
    /// What makes it decidable at all is sensor noise. A live camera looking at a perfectly still
    /// scene still produces a slightly different tile grid every frame, because the noise is in
    /// the sensor and not in the scene. So the threshold does not ask "is the scene still" - which
    /// would be unanswerable, a static settings screen being a legitimate picture - it asks
    /// **"is this the same data again"**, and the answer is yes only below the noise a working rig
    /// cannot help producing.
    ///
    /// The measure is the mean absolute difference of tile means between consecutive frames. Mean
    /// rather than max, so one tile catching a reflection cannot hide a screen that has otherwise
    /// stopped; tile means rather than pixels, because the reduction is already done and paid for.
    ///
    /// **The threshold cannot be guessed and has to be measured.** A run reports the quietest
    /// change it saw through <see cref="Propose"/>, and that is the floor the threshold has to sit
    /// under. Set it too high and a static screen is a fault; too low and nothing is ever one.
    /// </summary>
    public sealed class FreezeDetector
    {
        private const int HistogramBuckets = 2000;
        private const double HistogramMax = 2.0;      // luma per frame; above this is plainly live

        private readonly FreezeThresholds _t;
        private readonly SustainedFaultTracker _run = new SustainedFaultTracker(AnomalyKind.Freeze);
        private readonly TileGrid _previous = new TileGrid();
        private readonly long[] _histogram = new long[HistogramBuckets];

        private bool _hasPrevious;
        private long _judged;
        private long _counted;
        private double _quietest = double.MaxValue;

        public FreezeDetector(FreezeThresholds thresholds)
        {
            _t = thresholds ?? new FreezeThresholds();
            _t.Normalize();
        }

        /// <summary>Mean absolute change in tile means on the last frame, in luma.</summary>
        public double LastChange { get; private set; }

        public bool InFreeze => _run.InFault;
        public int RunningFrames => _run.RunningFrames;
        public double RunningMs => _run.RunningMs;
        public long Judged => _judged;
        public bool RateKnown => _run.RateKnown;
        public long FramesSkippedForGaps => _run.FramesSkippedForGaps;
        public bool EnteredThisFrame => _run.EnteredThisFrame;
        public bool RecoveredThisFrame => _run.RecoveredThisFrame;

        /// <summary>
        /// Judges one frame against the one before it.
        ///
        /// The first frame of a run establishes the reference and is not judged - there is no
        /// change to measure across nothing.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return null;

            if (!_hasPrevious)
            {
                _previous.CopyFrom(grid);
                _hasPrevious = true;
                // Still advance the tracker so it learns the frame period and sees the numbering.
                _run.Observe(grid.FrameNumber, timeStampSec, false, false, 0.0,
                             _t.EnterMs, _t.RecoverMs);
                return null;
            }

            double change = Change(_previous, grid);
            _previous.CopyFrom(grid);
            LastChange = change;
            _judged++;

            bool stopped = change <= _t.EnterDelta;
            bool moving = change >= _t.ExitDelta;

            // The noise floor, from frames that are not a fault. A frozen stretch would otherwise
            // define the floor as its own near-zero change and make the threshold unreachable.
            if (!_run.InFault && !stopped)
            {
                if (change < _quietest) _quietest = change;
                int b = (int)(change / HistogramMax * HistogramBuckets);
                if (b < 0) b = 0;
                if (b >= HistogramBuckets) b = HistogramBuckets - 1;
                _histogram[b]++;
                _counted++;
            }

            // How far under the level it went, as a fraction of it, so the report carries a number
            // of the same shape the other kinds do. 1.0 means the frames were identical.
            double under = _t.EnterDelta > 0.0 ? (_t.EnterDelta - change) / _t.EnterDelta : 0.0;
            if (under < 0.0) under = 0.0;
            if (under > 1.0) under = 1.0;

            return _run.Observe(grid.FrameNumber, timeStampSec, stopped, moving,
                                under, _t.EnterMs, _t.RecoverMs);
        }

        /// <summary>Reports a freeze still running but unconfirmed when acquisition stops.</summary>
        public AnomalyEvent? Flush() => _run.Flush();

        /// <summary>
        /// What the run says the threshold could be: half the quietest live change it measured.
        ///
        /// Half rather than a fitted quantile because the quantity is a hard floor. Anything below
        /// the noise a live rig cannot help producing is not a still picture, it is the same
        /// picture again - and half leaves room for a quieter hour than the one measured.
        /// </summary>
        public FreezeProposal Propose()
        {
            double quietest = _quietest == double.MaxValue ? 0.0 : _quietest;
            return new FreezeProposal(quietest, Median(), _judged, quietest / 2.0);
        }

        public void Reset()
        {
            _run.Reset();
            _hasPrevious = false;
            _previous.Reset();
            _judged = 0;
            _counted = 0;
            _quietest = double.MaxValue;
            Array.Clear(_histogram, 0, _histogram.Length);
            LastChange = 0.0;
        }

        /// <summary>
        /// Mean absolute difference of the tile means, in luma.
        ///
        /// Mean and not max: one tile catching a reflection must not hide a screen that has
        /// otherwise stopped. Tiles that are not populated in both frames are skipped rather than
        /// counted as unchanged, because an absent tile is not a still one.
        /// </summary>
        public static double Change(TileGrid before, TileGrid after)
        {
            if (before == null || after == null) return 0.0;

            double sum = 0.0;
            int seen = 0;
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                if (!before.IsPopulated(i) || !after.IsPopulated(i)) continue;
                sum += Math.Abs(after.Mean(i) - before.Mean(i));
                seen++;
            }
            return seen < 1 ? 0.0 : sum / seen;
        }

        private double Median()
        {
            if (_counted < 1) return 0.0;
            long half = _counted / 2, running = 0;
            for (int b = 0; b < HistogramBuckets; b++)
            {
                running += _histogram[b];
                if (running >= half)
                    return (b + 0.5) * HistogramMax / HistogramBuckets;
            }
            return HistogramMax;
        }
    }
}
