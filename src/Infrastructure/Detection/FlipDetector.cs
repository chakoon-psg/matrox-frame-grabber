using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Which way a picture was turned over.</summary>
    public enum FlipAxis
    {
        /// <summary>Not turned over.</summary>
        None = 0,
        /// <summary>Left and right exchanged.</summary>
        Horizontal = 1,
        /// <summary>Top and bottom exchanged.</summary>
        Vertical = 2,
        /// <summary>Both, which is the same as a half turn.</summary>
        Both = 3,
    }

    /// <summary>
    /// What counts as the picture having been turned over. Per camera.
    ///
    /// **No level here is a brightness.** `CONTEXT.md` is blunt about why: every brightness-derived
    /// scalar is invariant under a flip, so nothing measured in luma can see this at all. What
    /// these thresholds bound is a *comparison* - how well the new frame matches the mirror of the
    /// old one, against how well it matches the old one unturned.
    /// </summary>
    public sealed class FlipThresholds
    {
        /// <summary>
        /// How much better the mirrored match has to be than the straight one.
        ///
        /// A ratio, not a level, so it does not move with brightness or with how busy the picture
        /// is. 0.25 means the mirror has to fit four times better.
        /// </summary>
        public double MaxMatchRatio { get; set; } = 0.25;

        /// <summary>
        /// How different the two frames have to be before the comparison means anything, in luma.
        ///
        /// On two nearly identical frames every arrangement matches well and the ratio is noise
        /// over noise. A flip makes the straight comparison large - that is what a flip *is* - so
        /// requiring it costs nothing and removes the whole quiet-picture failure mode.
        /// </summary>
        public double MinChange { get; set; } = 1.0;

        /// <summary>
        /// How asymmetric the picture has to be about the axis in question, in luma.
        ///
        /// **The limit that cannot be designed away.** A picture symmetric about an axis is
        /// identical to its own mirror, so turning it over changes nothing and no measurement can
        /// say it happened. Rather than report a coin toss, the detector says nothing and the
        /// figure is on screen so the silence is visible.
        /// </summary>
        public double MinAsymmetry { get; set; } = 2.0;

        /// <summary>How long it has to stay turned over before it is a fault, in ms.</summary>
        public double EnterMs { get; set; } = 1000.0;

        /// <summary>How long it has to be back the right way before the fault is over, in ms.</summary>
        public double RecoverMs { get; set; } = 1000.0;

        /// <summary>Luma below which nothing is judged - a dark frame has no arrangement.</summary>
        public double MinLuma { get; set; } = 12.0;

        public void CopyFrom(FlipThresholds other)
        {
            if (other == null) return;
            MaxMatchRatio = Clamp(other.MaxMatchRatio, 1e-3, 1.0);
            MinChange = Clamp(other.MinChange, 0.0, 255.0);
            MinAsymmetry = Clamp(other.MinAsymmetry, 0.0, 255.0);
            EnterMs = Clamp(other.EnterMs, 1.0, 600000.0);
            RecoverMs = Clamp(other.RecoverMs, 1.0, 600000.0);
            MinLuma = Clamp(other.MinLuma, 0.0, 255.0);
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }

    /// <summary>What a run says the flip thresholds could be.</summary>
    public readonly struct FlipProposal
    {
        public FlipProposal(double bestRatio, double medianAsymmetry, long framesJudged,
                            long framesTooSymmetric, double suggestedMaxRatio)
        {
            BestRatio = bestRatio;
            MedianAsymmetry = medianAsymmetry;
            FramesJudged = framesJudged;
            FramesTooSymmetric = framesTooSymmetric;
            SuggestedMaxRatio = suggestedMaxRatio;
        }

        /// <summary>
        /// The best a mirror ever fit when the picture had not been turned over.
        ///
        /// **A minimum, so a longer run lowers it** - the opposite direction from Dropout's floor.
        /// A short run measures a ratio that is too high and proposes a threshold that would fire
        /// on an ordinary frame.
        /// </summary>
        public double BestRatio { get; }

        /// <summary>
        /// Typical asymmetry of what these panels display, in luma.
        ///
        /// The figure that says whether this kind can work here at all: a rig showing a symmetric
        /// picture cannot be watched for flips by any means, and this is the number that shows it.
        /// </summary>
        public double MedianAsymmetry { get; }

        public long FramesJudged { get; }

        /// <summary>Frames that could not be judged because the picture was too symmetric.</summary>
        public long FramesTooSymmetric { get; }

        /// <summary>Half the best a mirror ever fit on an unturned picture.</summary>
        public double SuggestedMaxRatio { get; }

        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames
                             && BestRatio > 0.0;

        public override string ToString() =>
            FramesJudged < 1
                ? $"flip proposal - no frame pair could be judged ({FramesTooSymmetric} too symmetric)"
                : $"flip proposal - ratio {SuggestedMaxRatio:F3} (best a mirror ever fit "
                + $"{BestRatio:F3}, typical asymmetry {MedianAsymmetry:F1} luma, "
                + $"{FramesJudged} judged, {FramesTooSymmetric} too symmetric to judge)"
                + (IsUsable ? string.Empty : " - NOT ENOUGH TO PROPOSE FROM");
    }

    /// <summary>
    /// Finds a picture that has been turned over.
    ///
    /// **Every brightness-derived number is blind to this**, and `CONTEXT.md` says so in as many
    /// words: mean, median, spread, standard deviation and every depth built on them are invariant
    /// under a mirror. The tile *arrangement* has to be compared, which is what this does.
    ///
    /// **The reference is the previous frame, not a stored layout.** A stored layout would have to
    /// be re-measured every time the screen changed what it displays, which on an AVN panel is
    /// constantly - so it would be wrong within seconds of being taken. The previous frame is
    /// always current, and it turns the question into one about the *moment* of the flip:
    ///
    ///     grid[t] matches mirror(grid[t-1]) far better than it matches grid[t-1]
    ///
    /// Nothing about the picture's content enters that comparison, which is the whole reason to
    /// build it this way.
    ///
    /// **Three things it cannot see, and each is a property of the measurement rather than a gap
    /// to be filled later:**
    ///
    /// **A rig that was already flipped when the run started.** There is no transition to catch,
    /// and with no stored layout there is nothing to compare against. This is the significant one:
    /// the detector answers "was the picture turned over during this run", not "is the picture the
    /// right way up". A commissioning check is a different act and belongs to a person.
    ///
    /// **A flip on a symmetric picture.** A picture symmetric about the axis is identical to its
    /// own mirror; turning it over changes nothing, and no measurement can say it happened. The
    /// asymmetry of what the panel is showing is reported so the silence is visible rather than
    /// implied.
    ///
    /// **A flip that coincides with the content changing completely.** Then neither arrangement
    /// matches and the frame is not judged.
    ///
    /// Once a flip is seen the state is latched, so the kind reports as sustained: the picture
    /// stays turned over until something turns it back, and that is one fault rather than one per
    /// frame.
    /// </summary>
    public sealed class FlipDetector
    {
        private readonly FlipThresholds _t;
        private readonly SustainedFaultTracker _run = new SustainedFaultTracker(AnomalyKind.Flip);

        private readonly double[] _previous = new double[TileGrid.TileCount];
        private bool _havePrevious;
        private bool _flipped;

        private long _judged;
        private long _tooSymmetric;
        private double _bestRatio = double.MaxValue;

        // Asymmetry is summarised by a running median over a small reservoir rather than a mean:
        // one frame of an arm across the lens would drag a mean and cannot move a median.
        private readonly double[] _asymmetries = new double[1024];
        private int _asymmetryCount;

        public FlipDetector(FlipThresholds thresholds)
        {
            _t = thresholds ?? new FlipThresholds();
        }

        /// <summary>Which mirror fit best on the last frame pair judged.</summary>
        public FlipAxis LastAxis { get; private set; }

        /// <summary>How well that mirror fit, over how well the unturned frame fit. Lower is a flip.</summary>
        public double LastRatio { get; private set; } = 1.0;

        /// <summary>How asymmetric the last frame was about that axis, in luma.</summary>
        public double LastAsymmetry { get; private set; }

        /// <summary>Which way the picture is turned right now, if it is.</summary>
        public FlipAxis Axis { get; private set; }

        public bool InFlip => _run.InFault;
        public int RunningFrames => _run.RunningFrames;
        public double RunningMs => _run.RunningMs;

        /// <summary>Frame pairs the comparison could actually be made on.</summary>
        public long Judged => _judged;

        /// <summary>Frame pairs skipped because the picture was symmetric about every axis.</summary>
        public long TooSymmetric => _tooSymmetric;

        public bool RateKnown => _run.RateKnown;
        public long FramesSkippedForGaps => _run.FramesSkippedForGaps;
        public bool EnteredThisFrame => _run.EnteredThisFrame;
        public bool RecoveredThisFrame => _run.RecoveredThisFrame;

        /// <summary>Judges one frame against the one before it.</summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return null;

            if (grid.TileMedian() < _t.MinLuma)
            {
                // A dark frame has no arrangement, and the frame after it must not be compared
                // against darkness - a picture coming back would match nothing and read as a flip.
                _havePrevious = false;
                return _run.Observe(grid.FrameNumber, timeStampSec, _flipped, !_flipped,
                                    0.0, _t.EnterMs, _t.RecoverMs);
            }

            if (!_havePrevious)
            {
                Remember(grid);
                return _run.Observe(grid.FrameNumber, timeStampSec, _flipped, !_flipped,
                                    0.0, _t.EnterMs, _t.RecoverMs);
            }

            double straight = Difference(grid, FlipAxis.None);
            FlipAxis best = FlipAxis.None;
            double bestDiff = double.MaxValue;
            double bestAsymmetry = 0.0;

            foreach (FlipAxis axis in new[] { FlipAxis.Horizontal, FlipAxis.Vertical, FlipAxis.Both })
            {
                double asymmetry = Asymmetry(grid, axis);
                if (asymmetry < _t.MinAsymmetry)
                    continue;                       // this axis cannot be decided on this picture

                double diff = Difference(grid, axis);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = axis;
                    bestAsymmetry = asymmetry;
                }
            }

            Remember(grid);

            if (best == FlipAxis.None)
            {
                // Symmetric about every axis. Not a fault and not a measurement - and the latch is
                // left exactly as it was, because a symmetric picture says nothing about whether
                // the screen is still turned over.
                _tooSymmetric++;
                LastAxis = FlipAxis.None;
                LastRatio = 1.0;
                LastAsymmetry = 0.0;
                return _run.Observe(grid.FrameNumber, timeStampSec, _flipped, !_flipped,
                                    0.0, _t.EnterMs, _t.RecoverMs);
            }

            LastAxis = best;
            LastAsymmetry = bestAsymmetry;
            Record(bestAsymmetry);

            // Two frames too alike to tell apart. A flip always makes the straight comparison
            // large, so requiring it costs nothing and removes the quiet-picture failure mode.
            if (straight < _t.MinChange)
            {
                LastRatio = 1.0;
                return _run.Observe(grid.FrameNumber, timeStampSec, _flipped, !_flipped,
                                    0.0, _t.EnterMs, _t.RecoverMs);
            }

            double ratio = bestDiff / straight;
            LastRatio = ratio;
            _judged++;

            if (!_flipped && ratio < _bestRatio) _bestRatio = ratio;

            if (ratio <= _t.MaxMatchRatio)
            {
                // The picture turned over on this frame. Turning over again is turning back, which
                // is why this toggles rather than sets - a screen that flips twice is upright.
                _flipped = !_flipped;
                Axis = _flipped ? best : FlipAxis.None;
            }

            // Deviation reported as how much better the mirror fit, so a larger number is a
            // stronger finding - the same direction as every other kind's.
            double deviation = 1.0 - ratio;
            if (deviation < 0.0) deviation = 0.0;

            return _run.Observe(grid.FrameNumber, timeStampSec, _flipped, !_flipped,
                                _flipped ? deviation : 0.0, _t.EnterMs, _t.RecoverMs);
        }

        /// <summary>Reports a flip still running but unconfirmed when acquisition stops.</summary>
        public AnomalyEvent? Flush() => _run.Flush();

        public FlipProposal Propose()
        {
            double best = _bestRatio == double.MaxValue ? 0.0 : _bestRatio;
            return new FlipProposal(best, MedianAsymmetry(), _judged, _tooSymmetric, best / 2.0);
        }

        public void Reset()
        {
            _run.Reset();
            _havePrevious = false;
            _flipped = false;
            Axis = FlipAxis.None;
            _judged = 0;
            _tooSymmetric = 0;
            _bestRatio = double.MaxValue;
            _asymmetryCount = 0;
            LastAxis = FlipAxis.None;
            LastRatio = 1.0;
            LastAsymmetry = 0.0;
        }

        private void Remember(TileGrid grid)
        {
            for (int i = 0; i < TileGrid.TileCount; i++)
                _previous[i] = grid.IsPopulated(i) ? grid.Mean(i) : double.NaN;
            _havePrevious = true;
        }

        /// <summary>
        /// Mean absolute difference between this frame and the previous one, with the previous
        /// one turned over about <paramref name="axis"/>.
        /// </summary>
        private double Difference(TileGrid grid, FlipAxis axis)
        {
            double sum = 0.0;
            int seen = 0;
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                if (!grid.IsPopulated(i)) continue;
                double was = _previous[Mirror(i, axis)];
                if (double.IsNaN(was)) continue;
                sum += Math.Abs(grid.Mean(i) - was);
                seen++;
            }
            return seen < 1 ? double.MaxValue : sum / seen;
        }

        /// <summary>
        /// How different this frame is from its own mirror about <paramref name="axis"/>.
        ///
        /// Zero means turning it over would change nothing, and then nothing can be concluded
        /// either way.
        /// </summary>
        public static double Asymmetry(TileGrid grid, FlipAxis axis)
        {
            if (grid == null) return 0.0;
            double sum = 0.0;
            int seen = 0;
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                int j = Mirror(i, axis);
                if (!grid.IsPopulated(i) || !grid.IsPopulated(j)) continue;
                sum += Math.Abs(grid.Mean(i) - grid.Mean(j));
                seen++;
            }
            return seen < 1 ? 0.0 : sum / seen;
        }

        /// <summary>Where tile <paramref name="tile"/> lands after the grid is turned over.</summary>
        public static int Mirror(int tile, FlipAxis axis)
        {
            int row = tile / TileGrid.Columns;
            int col = tile % TileGrid.Columns;
            if (axis == FlipAxis.Horizontal || axis == FlipAxis.Both)
                col = TileGrid.Columns - 1 - col;
            if (axis == FlipAxis.Vertical || axis == FlipAxis.Both)
                row = TileGrid.Rows - 1 - row;
            return row * TileGrid.Columns + col;
        }

        private void Record(double asymmetry)
        {
            if (_asymmetryCount < _asymmetries.Length)
            {
                _asymmetries[_asymmetryCount++] = asymmetry;
                return;
            }

            // Full: keep the reservoir current rather than frozen on the first thousand frames,
            // by replacing one at a stride. The summary is a scale, not a statistic anybody
            // computes a threshold from, so an exact median over the whole run is not worth
            // holding a run's worth of doubles for.
            _asymmetries[(int)(_judged % _asymmetries.Length)] = asymmetry;
        }

        private double MedianAsymmetry()
        {
            if (_asymmetryCount < 1) return 0.0;
            var copy = new double[_asymmetryCount];
            Array.Copy(_asymmetries, copy, _asymmetryCount);
            Array.Sort(copy);
            int n = copy.Length;
            return (n & 1) == 1 ? copy[n / 2] : (copy[n / 2 - 1] + copy[n / 2]) / 2.0;
        }
    }
}
