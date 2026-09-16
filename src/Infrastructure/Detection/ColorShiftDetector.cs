using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What counts as the colour being wrong. Per camera, and **calibrated rather than chosen**.
    ///
    /// Every other kind here has a threshold that can be argued for from first principles: a panel
    /// that is off is dark, a saturated one has no structure, a stopped one does not change. Colour
    /// has no such level. "Correct" is whatever this camera, this lens and this panel produce
    /// together, so the only defensible reference is a measured one - which is why
    /// <see cref="BaselineR"/> and friends start at zero, meaning *not yet calibrated*, and the
    /// detector stays silent until a run fills them in.
    /// </summary>
    public sealed class ColorShiftThresholds
    {
        /// <summary>Measured share of the frame's colour that band 0 carries. 0 = uncalibrated.</summary>
        public double BaselineR { get; set; }

        /// <summary>Measured share of band 1. 0 = uncalibrated.</summary>
        public double BaselineG { get; set; }

        /// <summary>Measured share of band 2. 0 = uncalibrated.</summary>
        public double BaselineB { get; set; }

        /// <summary>
        /// How far any one band's share may move from its baseline before the colour counts as
        /// wrong. In share, so 0.05 means five percentage points of the frame's total colour.
        ///
        /// The default is deliberately loose. A shift big enough to report is a band missing
        /// (0.33 of a move), two bands exchanged (the difference between their shares) or a white
        /// balance that has collapsed - all far past a tenth. Setting it tight is how this kind
        /// starts reporting the picture's own colour.
        /// </summary>
        public double MaxDeviation { get; set; } = 0.10;

        /// <summary>Deviation it has to fall back to before the fault is over. Below the entry.</summary>
        public double ExitDeviation { get; set; } = 0.07;

        /// <summary>
        /// Luma below which the shares are not judged at all.
        ///
        /// A dark frame's three bands are sensor noise, and noise divided by noise is a share that
        /// swings across its whole range. A panel that is off is Blackout's to report, not this
        /// one's - so below this level the detector says nothing rather than saying something
        /// wrong. Measured pedestal on this rig is about 5 luma.
        /// </summary>
        public double MinLuma { get; set; } = 12.0;

        /// <summary>How long the colour has to be wrong before it is a fault, in ms.</summary>
        public double EnterMs { get; set; } = 1000.0;

        /// <summary>How long it has to be back before the fault is over, in ms.</summary>
        public double RecoverMs { get; set; } = 500.0;

        /// <summary>Whether a baseline has been measured. Without one nothing is judged.</summary>
        public bool Calibrated => BaselineR > 0.0 && BaselineG > 0.0 && BaselineB > 0.0;

        public void CopyFrom(ColorShiftThresholds other)
        {
            if (other == null) return;
            BaselineR = Clamp(other.BaselineR, 0.0, 1.0);
            BaselineG = Clamp(other.BaselineG, 0.0, 1.0);
            BaselineB = Clamp(other.BaselineB, 0.0, 1.0);
            MaxDeviation = Clamp(other.MaxDeviation, 0.0, 1.0);
            ExitDeviation = Clamp(other.ExitDeviation, 0.0, 1.0);
            MinLuma = Clamp(other.MinLuma, 0.0, 255.0);
            EnterMs = Clamp(other.EnterMs, 1.0, 600000.0);
            RecoverMs = Clamp(other.RecoverMs, 1.0, 600000.0);
            Normalize();
        }

        /// <summary>Makes the pair a hysteresis: the exit sits below the entry.</summary>
        public void Normalize()
        {
            if (ExitDeviation >= MaxDeviation) ExitDeviation = MaxDeviation * 0.7;
            if (ExitDeviation < 0.0) ExitDeviation = 0.0;
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }

    /// <summary>What a run says the colour baseline and its tolerance could be.</summary>
    public readonly struct ColorShiftProposal
    {
        public ColorShiftProposal(double r, double g, double b, double floor, long framesJudged,
                                  double suggestedMaxDeviation)
        {
            BaselineR = r;
            BaselineG = g;
            BaselineB = b;
            Floor = floor;
            FramesJudged = framesJudged;
            SuggestedMaxDeviation = suggestedMaxDeviation;
        }

        /// <summary>Mean share of band 0 over the frames judged.</summary>
        public double BaselineR { get; }

        /// <summary>Mean share of band 1.</summary>
        public double BaselineG { get; }

        /// <summary>Mean share of band 2.</summary>
        public double BaselineB { get; }

        /// <summary>
        /// The furthest any band's share got from its own mean during the run.
        ///
        /// **A maximum, like Dropout's floor and unlike Blackout's** - a longer run can only raise
        /// it, so a short run proposes a threshold that is too tight. And unlike the other kinds
        /// this floor mixes two things that cannot be separated from frame statistics: the
        /// sensor's own colour noise, and the panel actually displaying different colours. A run
        /// against a static screen measures only the first and will look far quieter than the rig
        /// is.
        /// </summary>
        public double Floor { get; }

        public long FramesJudged { get; }

        /// <summary>Twice the floor, the margin this repo uses when setting a threshold by hand.</summary>
        public double SuggestedMaxDeviation { get; }

        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames && Floor > 0.0;

        public override string ToString() =>
            FramesJudged < 1
                ? $"colorshift proposal - no frame was bright enough to judge ({FramesJudged} frames)"
                : $"colorshift proposal - baseline R {BaselineR:F4} G {BaselineG:F4} B {BaselineB:F4}, "
                + $"deviation floor {Floor:F4}, suggest max {SuggestedMaxDeviation:F4} "
                + $"({FramesJudged} frames judged)"
                + (IsUsable ? string.Empty : " - NOT ENOUGH TO PROPOSE FROM");
    }

    /// <summary>
    /// Finds colour that has come away from what this camera measured as its own.
    ///
    /// **The one kind luma cannot see.** `CONTEXT.md` puts it plainly: collapse the bands into one
    /// number and an R-B exchange cancels exactly, so no brightness-derived figure moves at all.
    /// The bands have to be looked at separately, and they are - `TileReducer` already reads all
    /// three bytes per pixel to weight them into luma, so carrying three more sums costs three
    /// adds and no extra memory traffic.
    ///
    /// It judges **shares** - each band over the three together - rather than levels. A share does
    /// not move when the picture gets brighter or dimmer, which is the whole point: this kind has
    /// to be blind to everything the other four are looking at, or it reports their faults again
    /// under its own name.
    ///
    /// Three failures reach it, and the glossary names all three:
    ///
    /// **A band missing.** Its share goes to zero, a third of the range, past any threshold.
    ///
    /// **Two bands exchanged.** Their shares swap, so the move is the gap between them. On a
    /// neutral grey panel that gap is small and this is genuinely hard to see; on anything with
    /// colour in it the gap is large. The limit is real and it is written here rather than
    /// implied.
    ///
    /// **White balance collapsed.** All three drift together away from the baseline.
    ///
    /// **The false-positive path is the picture's own colour, and it cannot be separated from a
    /// fault by these statistics.** A screen that changes from a blue map to a red warning moves
    /// the shares exactly as a white balance failure would, and holds the new value - so dwell
    /// does not help either. What bounds it is the measured tolerance plus a threshold loose
    /// enough that only a band-sized move clears it. If these panels start showing varied colour,
    /// this kind has to be re-measured or switched off; it is not a detector that can be left to
    /// look after itself.
    ///
    /// Frame-level rather than per tile, so a colour fault in one corner is not covered. That is a
    /// resolution nothing has asked for, and 64 tiles x 3 bands would cost cache in the
    /// acquisition hook to provide it.
    /// </summary>
    public sealed class ColorShiftDetector
    {
        private readonly ColorShiftThresholds _t;
        private readonly SustainedFaultTracker _run =
            new SustainedFaultTracker(AnomalyKind.ColorShift);

        private long _judged;
        private double _sumR, _sumG, _sumB;
        private double _minR = double.MaxValue, _minG = double.MaxValue, _minB = double.MaxValue;
        private double _maxR = double.MinValue, _maxG = double.MinValue, _maxB = double.MinValue;

        public ColorShiftDetector(ColorShiftThresholds thresholds)
        {
            _t = thresholds ?? new ColorShiftThresholds();
            _t.Normalize();
        }

        /// <summary>Share of band 0 on the last frame bright enough to judge.</summary>
        public double LastShareR { get; private set; }

        /// <summary>Share of band 1 on the last frame judged.</summary>
        public double LastShareG { get; private set; }

        /// <summary>Share of band 2 on the last frame judged.</summary>
        public double LastShareB { get; private set; }

        /// <summary>Furthest any band sat from its baseline on the last frame. 0 if uncalibrated.</summary>
        public double LastDeviation { get; private set; }

        public bool InColorShift => _run.InFault;
        public int RunningFrames => _run.RunningFrames;
        public double RunningMs => _run.RunningMs;

        /// <summary>Frames bright enough to have their shares judged. Dark frames are not counted.</summary>
        public long Judged => _judged;

        public bool RateKnown => _run.RateKnown;
        public long FramesSkippedForGaps => _run.FramesSkippedForGaps;
        public bool EnteredThisFrame => _run.EnteredThisFrame;
        public bool RecoveredThisFrame => _run.RecoveredThisFrame;

        /// <summary>Whether this detector has a reference to judge against at all.</summary>
        public bool Calibrated => _t.Calibrated;

        /// <summary>
        /// Judges one frame. Returns the event on the frame the dwell confirms it, null otherwise.
        ///
        /// A grid without per-band totals - a mono ring, or a channel where nothing asked for them
        /// - is not an error and not a fault. It returns null and does not count the frame, so the
        /// proposal cannot be built out of frames that carried no colour.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null || !grid.HasBands)
                return null;

            double r = grid.BandMeanR, g = grid.BandMeanG, b = grid.BandMeanB;
            double total = r + g + b;

            // Too dark for a share to mean anything. Not a fault, not a measurement - the frame
            // simply does not carry the evidence, and a detector that guessed here would fire
            // every time the panel switched off, under the wrong kind's name.
            if (grid.TileMedian() < _t.MinLuma || total <= 0.0)
                return _run.Observe(grid.FrameNumber, timeStampSec, false, true,
                                    0.0, _t.EnterMs, _t.RecoverMs);

            double shareR = r / total, shareG = g / total, shareB = b / total;
            LastShareR = shareR;
            LastShareG = shareG;
            LastShareB = shareB;
            _judged++;

            _sumR += shareR; _sumG += shareG; _sumB += shareB;
            if (shareR < _minR) _minR = shareR;
            if (shareG < _minG) _minG = shareG;
            if (shareB < _minB) _minB = shareB;
            if (shareR > _maxR) _maxR = shareR;
            if (shareG > _maxG) _maxG = shareG;
            if (shareB > _maxB) _maxB = shareB;

            if (!_t.Calibrated)
            {
                // Measuring, not judging. The run still builds a proposal so the next one has a
                // reference - but a detector with no reference must not invent one mid-run and
                // start reporting against it, or the first frames of a run decide what "correct"
                // means for the rest of it.
                LastDeviation = 0.0;
                return null;
            }

            double dev = Math.Max(Math.Abs(shareR - _t.BaselineR),
                         Math.Max(Math.Abs(shareG - _t.BaselineG),
                                  Math.Abs(shareB - _t.BaselineB)));
            LastDeviation = dev;

            return _run.Observe(grid.FrameNumber, timeStampSec,
                                dev >= _t.MaxDeviation, dev <= _t.ExitDeviation,
                                dev, _t.EnterMs, _t.RecoverMs);
        }

        /// <summary>Reports a shift still running but unconfirmed when acquisition stops.</summary>
        public AnomalyEvent? Flush() => _run.Flush();

        public ColorShiftProposal Propose()
        {
            if (_judged < 1)
                return new ColorShiftProposal(0.0, 0.0, 0.0, 0.0, 0, 0.0);

            double mr = _sumR / _judged, mg = _sumG / _judged, mb = _sumB / _judged;

            // The furthest excursion from the mean, on whichever side and whichever band. Both
            // sides, because a share that only ever fell would otherwise measure as quiet.
            double floor = Math.Max(Math.Max(_maxR - mr, mr - _minR),
                           Math.Max(Math.Max(_maxG - mg, mg - _minG),
                                    Math.Max(_maxB - mb, mb - _minB)));

            return new ColorShiftProposal(mr, mg, mb, floor, _judged, floor * 2.0);
        }

        public void Reset()
        {
            _run.Reset();
            _judged = 0;
            _sumR = _sumG = _sumB = 0.0;
            _minR = _minG = _minB = double.MaxValue;
            _maxR = _maxG = _maxB = double.MinValue;
            LastShareR = LastShareG = LastShareB = 0.0;
            LastDeviation = 0.0;
        }
    }
}
