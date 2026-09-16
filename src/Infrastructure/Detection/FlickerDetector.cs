using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What counts as the picture flickering. Per camera.
    ///
    /// **Relative rather than absolute**, because a flicker is a proportion of the light: the same
    /// modulation on a panel reading 117 luma and one reading 35 is the same fault, and an
    /// amplitude in luma would catch one and miss the other. The three cameras here sit at
    /// 116.9 / 82.6 / 34.9, a factor of three apart, which makes this the difference between one
    /// threshold and three.
    /// </summary>
    public sealed class FlickerThresholds
    {
        /// <summary>
        /// How far the brightness has to swing, as a fraction of its own mean over the window.
        ///
        /// Peak to trough, using the 10th and 90th percentiles rather than the extremes - one
        /// dropped frame or one arm crossing the lens should not set the amplitude for a second
        /// of video.
        /// </summary>
        public double EnterAmplitude { get; set; } = 0.06;

        /// <summary>Amplitude it has to fall back under before the fault is over.</summary>
        public double ExitAmplitude { get; set; } = 0.04;

        /// <summary>
        /// Slowest oscillation still called a flicker, in Hz.
        ///
        /// Below this the window is watching content change, or a panel dimming - both of which
        /// swing the brightness without oscillating. **A backlight beating slowly against the
        /// frame rate also lands here**, which is the sampling artefact `CONTEXT.md` says is the
        /// whole difficulty of this kind.
        /// </summary>
        public double MinHz { get; set; } = 3.0;

        /// <summary>
        /// Fastest oscillation still called a flicker, in Hz.
        ///
        /// Above this the signal is approaching what the frame rate can carry, and anything found
        /// up there is aliasing rather than something a person could see. Real backlight PWM runs
        /// in the kHz and cannot be sampled at 120 fps at all - what reaches this detector is its
        /// alias, and the alias is what has to be kept out.
        /// </summary>
        public double MaxHz { get; set; } = 25.0;

        /// <summary>
        /// Frames of brightness the window holds. At 120 fps 120 frames is one second, which is
        /// what sets the slowest oscillation that can be counted at all.
        /// </summary>
        public int WindowFrames { get; set; } = 120;

        /// <summary>
        /// How many times the brightness has to cross its own mean inside the window.
        ///
        /// **This is what separates a flicker from a dropout**, and it is a separate test from the
        /// frequency because a single deep dip produces two crossings and a frequency estimate
        /// that lands inside the band. An oscillation crosses over and over; an event crosses
        /// twice.
        /// </summary>
        public int MinCrossings { get; set; } = 6;

        /// <summary>How long the oscillation has to hold before it is a fault, in ms.</summary>
        public double EnterMs { get; set; } = 1000.0;

        /// <summary>How long it has to be steady before the fault is over, in ms.</summary>
        public double RecoverMs { get; set; } = 1000.0;

        /// <summary>
        /// Luma below which nothing is judged. A relative amplitude on a dark frame is noise
        /// divided by noise - the same reason ColorShift has one.
        /// </summary>
        public double MinLuma { get; set; } = 12.0;

        public void CopyFrom(FlickerThresholds other)
        {
            if (other == null) return;
            EnterAmplitude = Clamp(other.EnterAmplitude, 1e-4, 10.0);
            ExitAmplitude = Clamp(other.ExitAmplitude, 0.0, 10.0);
            MinHz = Clamp(other.MinHz, 0.1, 1000.0);
            MaxHz = Clamp(other.MaxHz, 0.2, 1000.0);
            WindowFrames = (int)Clamp(other.WindowFrames, 8, 100000);
            MinCrossings = (int)Clamp(other.MinCrossings, 2, 100000);
            EnterMs = Clamp(other.EnterMs, 1.0, 600000.0);
            RecoverMs = Clamp(other.RecoverMs, 1.0, 600000.0);
            MinLuma = Clamp(other.MinLuma, 0.0, 255.0);
            Normalize();
        }

        /// <summary>Makes the pairs a hysteresis and a band.</summary>
        public void Normalize()
        {
            if (ExitAmplitude >= EnterAmplitude) ExitAmplitude = EnterAmplitude * 0.7;
            if (ExitAmplitude < 0.0) ExitAmplitude = 0.0;
            if (MaxHz <= MinHz) MaxHz = MinHz * 2.0;
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
    }

    /// <summary>What a run says the flicker thresholds could be.</summary>
    public readonly struct FlickerProposal
    {
        public FlickerProposal(double quietestAmplitude, double loudestAmplitude, long windows,
                               double suggestedEnterAmplitude)
        {
            QuietestAmplitude = quietestAmplitude;
            LoudestAmplitude = loudestAmplitude;
            Windows = windows;
            SuggestedEnterAmplitude = suggestedEnterAmplitude;
        }

        /// <summary>The calmest second the run saw, for scale.</summary>
        public double QuietestAmplitude { get; }

        /// <summary>
        /// The loudest amplitude measured in a window whose crossings were inside the band.
        ///
        /// **A maximum, so a longer run raises it** - the same direction as Dropout's floor, and
        /// the same consequence: a short run proposes a threshold that is too tight. Content
        /// changing does not usually oscillate, which is what makes this floor mean anything at
        /// all; a screen running an animation would raise it for a real reason.
        /// </summary>
        public double LoudestAmplitude { get; }

        /// <summary>Windows judged - frames minus the first window's worth.</summary>
        public long Windows { get; }

        /// <summary>Twice the loudest quiet window, the margin this repo uses by hand.</summary>
        public double SuggestedEnterAmplitude { get; }

        public bool IsUsable => Windows >= AnomalyDetector.MinCalibrationFrames
                             && LoudestAmplitude > 0.0;

        public override string ToString() =>
            Windows < 1
                ? $"flicker proposal - no window was bright enough to judge ({Windows} windows)"
                : $"flicker proposal - enter {SuggestedEnterAmplitude:F4} relative "
                + $"(loudest in-band quiet window {LoudestAmplitude:F4}, calmest "
                + $"{QuietestAmplitude:F4}, {Windows} windows judged)"
                + (IsUsable ? string.Empty : " - NOT ENOUGH TO PROPOSE FROM");
    }

    /// <summary>
    /// Finds brightness that is oscillating rather than stepping.
    ///
    /// **The hard part is not finding the oscillation, it is refusing the two things that look
    /// like one.** `CONTEXT.md` says as much: the whole difficulty of this term is telling a real
    /// flicker from the false one a backlight makes. So there are three tests, and only the first
    /// is about amplitude:
    ///
    /// **How far it swings** (<see cref="FlickerThresholds.EnterAmplitude"/>), as a fraction of
    /// the window mean - relative, because the three panels here differ threefold in brightness
    /// and the same modulation on each is the same fault.
    ///
    /// **How often it crosses its own mean** (<see cref="FlickerThresholds.MinCrossings"/>). This
    /// is what keeps a Dropout out. A single deep dip swings the amplitude as far as anything and
    /// crosses the mean exactly twice; an oscillation crosses over and over. Without this test
    /// every dropout would also be reported here, under a second name.
    ///
    /// **What rate that works out to** (<see cref="FlickerThresholds.MinHz"/> to
    /// <see cref="FlickerThresholds.MaxHz"/>). Below the band is a picture changing or a panel
    /// dimming; above it is aliasing. Real backlight PWM runs in the kHz and cannot be sampled at
    /// 120 fps at all - what arrives is its alias, and the alias is what the upper edge is for.
    ///
    /// **The band is a filter, not a proof.** A backlight beating slowly against the frame rate
    /// can land inside it, and nothing this detector can measure would tell that apart from a
    /// panel that is genuinely flickering. What tells them apart is an exposure sweep - the
    /// modulation follows |sinc(pi f T)| and a real fault does not - and that is
    /// `--expo-scan`, an offline act on a rig somebody is standing at. This detector reports;
    /// `CLAUDE.md` records why one measurement at one exposure cannot settle it.
    ///
    /// Sustained rather than event, and it has to be: a flicker is a state of the panel, and
    /// reporting each swing would produce one event per oscillation.
    /// </summary>
    public sealed class FlickerDetector
    {
        private readonly FlickerThresholds _t;
        private readonly SustainedFaultTracker _run = new SustainedFaultTracker(AnomalyKind.Flicker);

        private readonly double[] _window;
        private readonly double[] _sorted;
        private int _filled;
        private int _next;

        private long _windows;
        private double _quietest = double.MaxValue;
        private double _loudestQuiet = double.MinValue;

        public FlickerDetector(FlickerThresholds thresholds)
        {
            _t = thresholds ?? new FlickerThresholds();
            _t.Normalize();
            _window = new double[Math.Max(8, _t.WindowFrames)];
            _sorted = new double[_window.Length];
        }

        /// <summary>Relative swing across the last full window. 0 until the window fills.</summary>
        public double LastAmplitude { get; private set; }

        /// <summary>Oscillations per second implied by the crossings in the last window.</summary>
        public double LastHz { get; private set; }

        /// <summary>Times the brightness crossed its own mean in the last window.</summary>
        public int LastCrossings { get; private set; }

        public bool InFlicker => _run.InFault;
        public int RunningFrames => _run.RunningFrames;
        public double RunningMs => _run.RunningMs;

        /// <summary>Full windows judged.</summary>
        public long Judged => _windows;

        public bool RateKnown => _run.RateKnown;
        public long FramesSkippedForGaps => _run.FramesSkippedForGaps;
        public bool EnteredThisFrame => _run.EnteredThisFrame;
        public bool RecoveredThisFrame => _run.RecoveredThisFrame;

        /// <summary>
        /// Judges one frame. The window has to fill before anything is judged, so the first
        /// second of a run reports nothing - which is correct rather than a limitation: an
        /// oscillation is not a property of a frame.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return null;

            double median = grid.TileMedian();

            // Too dark for a relative amplitude to mean anything. The window is dropped rather
            // than fed, so a panel switching off does not leave half a second of darkness inside
            // the next second of picture and read as an enormous swing.
            if (median < _t.MinLuma)
            {
                _filled = 0;
                _next = 0;
                LastAmplitude = 0.0;
                LastHz = 0.0;
                LastCrossings = 0;
                return _run.Observe(grid.FrameNumber, timeStampSec, false, true,
                                    0.0, _t.EnterMs, _t.RecoverMs);
            }

            _window[_next] = median;
            _next = (_next + 1) % _window.Length;
            if (_filled < _window.Length) _filled++;

            // Still filling. The tracker is fed anyway, with nothing wrong: it needs
            // consecutive frame numbers to measure the rate, and skipping the first window would
            // make the first judged frame look like a 120-frame gap - which the tracker would
            // correctly treat as a hole in the numbering and restart the dwell for. It would also
            // count as a skipped frame in a figure that is supposed to mean dropped frames.
            if (_filled < _window.Length)
                return _run.Observe(grid.FrameNumber, timeStampSec, false, true,
                                    0.0, _t.EnterMs, _t.RecoverMs);

            Measure();
            _windows++;

            bool inBand = LastHz >= _t.MinHz && LastHz <= _t.MaxHz
                       && LastCrossings >= _t.MinCrossings;
            bool flickering = inBand && LastAmplitude >= _t.EnterAmplitude;
            bool steady = LastAmplitude <= _t.ExitAmplitude || !inBand;

            // The floor only counts windows that would have been believed but for the amplitude.
            // A window failing the band test says nothing about how loud a real flicker has to be.
            if (!_run.InFault && inBand)
            {
                if (LastAmplitude > _loudestQuiet) _loudestQuiet = LastAmplitude;
            }
            if (LastAmplitude < _quietest) _quietest = LastAmplitude;

            return _run.Observe(grid.FrameNumber, timeStampSec, flickering, steady,
                                LastAmplitude, _t.EnterMs, _t.RecoverMs);
        }

        /// <summary>Reports a flicker still running but unconfirmed when acquisition stops.</summary>
        public AnomalyEvent? Flush() => _run.Flush();

        public FlickerProposal Propose()
        {
            bool saw = _loudestQuiet != double.MinValue;
            double loud = saw ? _loudestQuiet : 0.0;
            double calm = _quietest == double.MaxValue ? 0.0 : _quietest;
            return new FlickerProposal(calm, loud, _windows, loud * 2.0);
        }

        public void Reset()
        {
            _run.Reset();
            _filled = 0;
            _next = 0;
            _windows = 0;
            _quietest = double.MaxValue;
            _loudestQuiet = double.MinValue;
            LastAmplitude = 0.0;
            LastHz = 0.0;
            LastCrossings = 0;
        }

        /// <summary>
        /// Amplitude, crossings and the rate they imply, over the full window.
        ///
        /// The rate comes from counting crossings rather than from a transform: one oscillation
        /// crosses its mean twice, so half the crossings over the window's duration is the
        /// frequency. It is coarse - it cannot separate two tones - and it is all this needs,
        /// because the question asked of it is only which side of a band the signal is on. An FFT
        /// in the acquisition hook would cost more than the reduction that feeds it.
        /// </summary>
        private void Measure()
        {
            int n = _window.Length;
            Array.Copy(_window, _sorted, n);
            Array.Sort(_sorted);

            // Percentiles rather than the extremes: one dropped frame, or one arm crossing the
            // lens, must not set the amplitude for a whole second of video.
            double low = _sorted[(int)(n * 0.10)];
            double high = _sorted[(int)(n * 0.90)];

            double sum = 0.0;
            for (int i = 0; i < n; i++) sum += _window[i];
            double mean = sum / n;

            LastAmplitude = mean > 0.0 ? (high - low) / mean : 0.0;

            // Crossings in arrival order, which is why the ring is read from the oldest sample
            // rather than from index zero.
            int crossings = 0;
            bool above = _window[_next] > mean;
            for (int i = 1; i < n; i++)
            {
                bool nowAbove = _window[(_next + i) % n] > mean;
                if (nowAbove != above) crossings++;
                above = nowAbove;
            }
            LastCrossings = crossings;

            // Frames, not seconds, until the tracker has measured the rate - and then in seconds,
            // because a frequency in "crossings per window" is not a number anyone can check.
            double windowSec = _run.RateKnown && _run.Fps > 0.0
                             ? n / _run.Fps
                             : 0.0;
            LastHz = windowSec > 0.0 ? crossings / 2.0 / windowSec : 0.0;
        }
    }
}
