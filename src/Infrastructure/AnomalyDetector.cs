using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The numbers the detector compares against. All of them belong in a settings file: room
    /// lighting and panel differ site to site, and changing one must not need a rebuild.
    /// </summary>
    public sealed class AnomalyThresholds
    {
        /// <summary>How far brightness must fall to count. 0.10 sits about 100x above sample noise.</summary>
        public double Depth { get; set; } = 0.10;

        /// <summary>
        /// How much the tiles must agree before a fall is believed. This is the whole defence
        /// against content changing, so it is the one to tune first if false positives appear.
        /// </summary>
        public double Coherence { get; set; } = 0.80;

        /// <summary>
        /// Clear frames required before an event is considered over. A flicker that stops and starts
        /// inside this window is one event, not several — the report counts faults, not frames.
        /// </summary>
        public int DebounceFrames { get; set; } = 20;

        /// <summary>Frames of history the running baseline holds.</summary>
        public int BaselineWindow { get; set; } = 91;

        /// <summary>
        /// Frames needed before any judgement is made. Without history there is nothing for a fall
        /// to be a fraction of, and every opening frame would read as a total blackout.
        /// </summary>
        public int BaselineWarmupFrames { get; set; } = 30;
    }

    /// <summary>
    /// One confirmed anomaly. The last three fields are the reporting unit the customer asked for —
    /// "8 ms at depth 1.00, once" rather than "it was in an anomalous state".
    /// </summary>
    public readonly struct AnomalyEvent
    {
        public long StartFrame { get; }
        public long EndFrame { get; }
        public int FrameCount { get; }
        public double MaxDepth { get; }
        public double MaxCoherence { get; }
        public double StartTimeSec { get; }
        public double DurationMs { get; }

        public AnomalyEvent(long startFrame, long endFrame, int frameCount,
                            double maxDepth, double maxCoherence,
                            double startTimeSec, double durationMs)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            FrameCount = frameCount;
            MaxDepth = maxDepth;
            MaxCoherence = maxCoherence;
            StartTimeSec = startTimeSec;
            DurationMs = durationMs;
        }

        public override string ToString() =>
            $"frame {StartFrame}-{EndFrame} ({FrameCount}), {DurationMs:F1} ms, " +
            $"depth {MaxDepth:F2}, coh {MaxCoherence:F2}";
    }

    /// <summary>
    /// Watches one channel's tile grids and reports transient blanking — the event-type anomaly the
    /// spec makes v1's body.
    ///
    /// Confirmation takes a single frame. A one-to-three-frame fault can never satisfy an N-frame
    /// rule, so requiring one would mean never detecting the thing this exists for. What defends
    /// against false positives instead is the coherence gate: a screen going dark moves every tile
    /// the same way, and content changing does not.
    ///
    /// Free of MIL and of threads. The reducer that fills grids and the thread that pumps them live
    /// on the other side of that line, which is what lets every rule here be checked with synthetic
    /// frames and no hardware.
    /// </summary>
    public sealed class AnomalyDetector
    {
        private readonly AnomalyThresholds _t;
        private readonly double[] _window;
        private int _windowCount;
        private int _windowNext;
        private long _observed;

        private TileGrid _previous;
        private long _previousFrame = -1;
        private double _previousTime;
        private double _framePeriodSec;

        // Open event
        private bool _inEvent;
        private long _startFrame, _lastDarkFrame;
        private double _startTime, _lastDarkTime;
        private int _darkFrames;
        private double _maxDepth, _maxCoherence;
        private int _clearFrames;

        public AnomalyDetector(AnomalyThresholds thresholds)
        {
            _t = thresholds ?? new AnomalyThresholds();
            _window = new double[Math.Max(1, _t.BaselineWindow)];
        }

        /// <summary>The running median of recent normal frames. Zero until warm.</summary>
        public double Baseline { get; private set; }

        /// <summary>True while an event is open and not yet emitted.</summary>
        public bool InEvent => _inEvent;

        /// <summary>Frames set aside because the numbering jumped — frames the board never delivered.</summary>
        public long FramesSkippedForGaps { get; private set; }

        /// <summary>
        /// Takes one frame. Returns an event when one has just closed, otherwise null.
        ///
        /// An event is emitted after the debounce passes without recurrence, so it arrives a few
        /// frames late. That is deliberate and harmless: the preroll that captures the clip is far
        /// longer than the delay.
        /// </summary>
        public AnomalyEvent? Observe(TileGrid grid, double timeStampSec)
        {
            if (grid == null)
                return null;

            long frame = grid.FrameNumber;

            // A jump in the numbering means the board could not deliver the frames between. There is
            // no delta to measure across a hole, and reading the hole as a fall is the first
            // false-positive path in this design — so this frame only re-establishes the reference.
            bool gap = _previousFrame >= 0 && frame != _previousFrame + 1;
            if (gap)
            {
                FramesSkippedForGaps++;
                AdoptAsReference(grid, timeStampSec);
                return null;
            }

            if (_previous != null && timeStampSec > _previousTime)
                _framePeriodSec = timeStampSec - _previousTime;

            double median = grid.TileMedian();
            double depth = FrameMetrics.Depth(median, Baseline);
            double coherence = FrameMetrics.Coherence(_previous, grid);

            AnomalyEvent? emitted = null;

            if (_observed < _t.BaselineWarmupFrames)
            {
                // Still learning what normal looks like. Judge nothing.
                Remember(median);
            }
            else if (IsAnomalous(depth, coherence))
            {
                if (!_inEvent)
                {
                    _inEvent = true;
                    _startFrame = frame;
                    _startTime = timeStampSec;
                    _darkFrames = 0;
                    _maxDepth = 0;
                    _maxCoherence = 0;
                }
                _clearFrames = 0;
                _darkFrames++;
                _lastDarkFrame = frame;
                _lastDarkTime = timeStampSec;
                if (depth > _maxDepth) _maxDepth = depth;
                if (coherence > _maxCoherence) _maxCoherence = coherence;

                // Deliberately not remembered: a fault must not become the new normal. If dark
                // frames fed the running median, a long enough blackout would erase itself.
            }
            else
            {
                Remember(median);

                if (_inEvent && ++_clearFrames >= _t.DebounceFrames)
                    emitted = Close();
            }

            _observed++;
            AdoptAsReference(grid, timeStampSec);
            return emitted;
        }

        /// <summary>
        /// Closes an open event, for when acquisition stops. Without it, a fault still running when
        /// the grab ends is never reported at all.
        /// </summary>
        public AnomalyEvent? Flush() => _inEvent ? Close() : (AnomalyEvent?)null;

        public void Reset()
        {
            Array.Clear(_window, 0, _window.Length);
            _windowCount = 0;
            _windowNext = 0;
            _observed = 0;
            Baseline = 0;

            _previous = null;
            _previousFrame = -1;
            _previousTime = 0;
            _framePeriodSec = 0;

            _inEvent = false;
            _clearFrames = 0;
            _darkFrames = 0;
            FramesSkippedForGaps = 0;
        }

        /// <summary>
        /// Whether this frame belongs to an anomaly.
        ///
        /// Entering needs both gates. Staying only needs depth — and that is not a loosening, it is
        /// what the second frame of a blank actually looks like. Once the screen is dark the tiles
        /// stop moving, so coherence falls to zero with nothing wrong; demanding it frame by frame
        /// would chop every multi-frame blank into a string of single-frame events. Coherence
        /// answers "did the whole picture move into this together", which is a question about the
        /// transition, not about the state.
        /// </summary>
        private bool IsAnomalous(double depth, double coherence)
        {
            if (depth <= _t.Depth)
                return false;
            return _inEvent || coherence > _t.Coherence;
        }

        private AnomalyEvent Close()
        {
            // Duration is frames times the frame period, not the span between the first and last
            // timestamps: the span leaves out the last frame's own exposure and under-reports a
            // two-frame blank by half. A single-frame event would report zero.
            double period = _framePeriodSec > 0 ? _framePeriodSec : 0;
            var e = new AnomalyEvent(
                _startFrame, _lastDarkFrame, _darkFrames,
                _maxDepth, _maxCoherence,
                _startTime, _darkFrames * period * 1000.0);

            _inEvent = false;
            _clearFrames = 0;
            return e;
        }

        private void Remember(double median)
        {
            _window[_windowNext] = median;
            _windowNext = (_windowNext + 1) % _window.Length;
            if (_windowCount < _window.Length) _windowCount++;
            Baseline = Median();
        }

        private double Median()
        {
            if (_windowCount == 0)
                return 0;

            Span<double> copy = stackalloc double[_windowCount];
            for (int i = 0; i < _windowCount; i++) copy[i] = _window[i];
            copy.Sort();

            return (_windowCount & 1) == 1
                 ? copy[_windowCount / 2]
                 : (copy[_windowCount / 2 - 1] + copy[_windowCount / 2]) / 2.0;
        }

        private void AdoptAsReference(TileGrid grid, double timeStampSec)
        {
            _previous = grid;
            _previousFrame = grid.FrameNumber;
            _previousTime = timeStampSec;
        }
    }
}
