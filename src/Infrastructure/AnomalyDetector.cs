using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The numbers the detector compares against. All of them belong in a settings file: room
    /// lighting and panel differ site to site, and changing one must not need a rebuild.
    /// </summary>
    public sealed class AnomalyThresholds
    {
        /// <summary>
        /// How far brightness must fall to count, as a fraction of the running baseline.
        ///
        /// Measured rather than guessed, and confirmed by running at it. On a healthy panel at
        /// 8000 us and 124.3 fps, the deepest fall on a frame whose tiles agreed -- the only frames
        /// the depth threshold has to turn away by itself -- came out as:
        ///
        ///     15 min, 111900 frames/channel:  0.0028  0.0109  0.0028
        ///     10 min,  74601 frames/channel:  0.0089  0.0184  0.0081   0 false positives
        ///
        /// So the floor is not a fixed number: it nearly doubled on the dimmest channel between two
        /// runs an hour apart. Take the worst seen, 0.0184, and 0.05 clears it by 2.7x -- thinner
        /// than the 4.5x the first run alone suggested, which is why the second run matters. What
        /// settles it is that 0.05 produced no false positive in 74601 frames on any channel.
        ///
        /// The channel setting the worst case is simply the dimmest: luma 53 against 65 and 66. The
        /// cheapest way to buy margin back is light on that camera, not a higher threshold.
        ///
        /// Chosen at 0.05 rather than left at 0.10 for sensitivity: on the depth-staircase clip,
        /// whose steps are 1 - code/128, a 0.10 threshold catches two of the eight and 0.05 catches
        /// four. Lower is not supported -- 0.03 would sit under twice the worst floor measured.
        ///
        /// One caveat travels with the number: both runs were on a static panel, where coherence
        /// turned away at most one frame in fifteen minutes, so the gate that is supposed to absorb
        /// content movement has never been under load. Re-measure with content moving before
        /// treating 0.05 as settled.
        /// </summary>
        public double Depth { get; set; } = DefaultDepth;

        /// <summary>
        /// The default <see cref="Depth"/>, as a constant, because PwmSweep derives the shortest
        /// detectable event from the same quantity and two copies of it would drift apart silently.
        /// </summary>
        public const double DefaultDepth = 0.05;

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

        /// <summary>
        /// How long one event may stay open before it is closed and the baseline re-adopted.
        /// 250 frames is about 2 s at 124.3 fps.
        ///
        /// Without a cap the detector can be silenced by content. While an event is open the
        /// baseline is deliberately frozen, so a sustained fall that came from the picture rather
        /// than a fault holds depth above the threshold indefinitely, the event never closes, and
        /// nothing is emitted while one is open. Measured: a channel entered on an 8% fall from
        /// moving content and then missed all seven true events of the following cycle, which the
        /// other two channels caught.
        ///
        /// The cost is that a genuinely sustained fault is reported once rather than continuously.
        /// It carries <see cref="AnomalyEvent.Truncated"/> so that reads as what it is.
        /// </summary>
        public int MaxEventFrames { get; set; } = 250;

        /// <summary>Frames of history the running baseline holds.</summary>
        public int BaselineWindow { get; set; } = 91;

        /// <summary>
        /// Frames needed before any judgement is made. Without history there is nothing for a fall
        /// to be a fraction of, and every opening frame would read as a total blackout.
        /// </summary>
        public int BaselineWarmupFrames { get; set; } = 30;

        /// <summary>
        /// Copies every threshold from <paramref name="other"/>, clamping each to a range that
        /// cannot silence the detector.
        ///
        /// Clamped rather than trusted because these come from a settings file a person edits. A
        /// depth of 0 fires on every frame, a depth of 1 fires on nothing, a coherence above 1 can
        /// never be satisfied, and a baseline window of 0 leaves nothing to compare against -- each
        /// of those turns the detector off in a way that looks like a quiet rig.
        /// </summary>
        public void CopyFrom(AnomalyThresholds other)
        {
            if (other == null) return;

            Depth = Clamp(other.Depth, 0.001, 0.999);
            Coherence = Clamp(other.Coherence, 0.0, 1.0);
            DebounceFrames = Clamp(other.DebounceFrames, 1, 100000);
            MaxEventFrames = Clamp(other.MaxEventFrames, 1, 1000000);
            BaselineWindow = Clamp(other.BaselineWindow, 3, 100000);
            BaselineWarmupFrames = Clamp(other.BaselineWarmupFrames, 1, 100000);
        }

        private static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
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

        /// <summary>
        /// Whether this event was closed by the duration cap rather than by the picture recovering.
        /// A truncated event says the fall was still going when counting stopped, so its duration is
        /// a floor rather than a measurement.
        /// </summary>
        public bool Truncated { get; }

        public AnomalyEvent(long startFrame, long endFrame, int frameCount,
                            double maxDepth, double maxCoherence,
                            double startTimeSec, double durationMs,
                            bool truncated = false)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            FrameCount = frameCount;
            MaxDepth = maxDepth;
            MaxCoherence = maxCoherence;
            StartTimeSec = startTimeSec;
            DurationMs = durationMs;
            Truncated = truncated;
        }

        public override string ToString() =>
            $"frame {StartFrame}-{EndFrame} ({FrameCount}), {DurationMs:F1} ms, " +
            $"depth {MaxDepth:F2}, coh {MaxCoherence:F2}" +
            (Truncated ? " (still running - duration is a floor)" : string.Empty);
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

        // Owned, and copied into: the caller reuses one grid per channel, so keeping its
        // reference would make the previous frame and the current one the same object.
        private readonly TileGrid _previous = new TileGrid();
        private bool _hasPrevious;
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

        /// <summary>
        /// What the last judged frame measured. These are here to be watched, not merely debugged:
        /// the thresholds have to be set from the distribution these take on a healthy panel, and
        /// that distribution is a property of the rig rather than something to reason out. Depth and
        /// coherence are the two numbers the gates compare, so they are the two worth recording.
        /// </summary>
        public double LastDepth { get; private set; }
        public double LastCoherence { get; private set; }
        public double LastMedian { get; private set; }

        /// <summary>Frames judged so far. Below BaselineWarmupFrames nothing is judged yet.</summary>
        public long Observed => _observed;

        /// <summary>
        /// The deepest fall on a normal frame whose tiles agreed -- coherence above its gate, so
        /// coherence offered no protection and only the depth threshold stood between it and an
        /// event. This is the number the depth threshold has to clear, and the ratio between them
        /// is the margin. It cannot be read off the sampled log, because it lives in the tail.
        /// </summary>
        public double MaxCoherentNormalDepth { get; private set; }

        /// <summary>
        /// The frame <see cref="MaxCoherentNormalDepth"/> came from, so the figure can be checked
        /// rather than trusted. It has to be: a fault does not start in one frame, and the frame
        /// before an event opens is partway down it. Measured over 111900 frames the worst normal
        /// depth was 0.0994 against a 0.10 threshold -- which reads as no margin at all until the
        /// frame number puts it half a second before an 800 ms blackout, with everything away from
        /// an event under 0.011. Without this the counter cannot tell the floor from a shoulder.
        /// </summary>
        public long MaxCoherentNormalDepthFrame { get; private set; }

        /// <summary>
        /// The deepest fall on any normal frame, agreeing tiles or not. Larger than
        /// <see cref="MaxCoherentNormalDepth"/> by however much content movement the coherence gate
        /// is turning away, which is what makes the pair worth reading together rather than either
        /// alone.
        /// </summary>
        public double MaxNormalDepth { get; private set; }

        /// <summary>
        /// Normal frames that got past half the depth threshold. A maximum can be one freak frame;
        /// this says whether the population crowds the gate or sits far below it.
        /// </summary>
        public long NormalFramesNearThreshold { get; private set; }

        /// <summary>
        /// Frames deep enough to fire that only the coherence gate turned away. How much work that
        /// gate is doing: a large number means the depth threshold is too low and the defence rests
        /// on coherence alone.
        /// </summary>
        public long CoherenceSaves { get; private set; }

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

            if (_hasPrevious && timeStampSec > _previousTime)
                _framePeriodSec = timeStampSec - _previousTime;

            double median = grid.TileMedian();
            double depth = FrameMetrics.Depth(median, Baseline);
            double coherence = _hasPrevious ? FrameMetrics.Coherence(_previous, grid) : 0.0;

            LastMedian = median;
            LastDepth = depth;
            LastCoherence = coherence;

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
                //
                // Which is exactly why the event needs a cap. Frozen baseline plus a fall that came
                // from the picture rather than a fault means depth never recovers, the event never
                // closes, and nothing is emitted while one is open -- the channel goes silent. So
                // past the cap the event is reported as still-running and the current level becomes
                // the new normal, which restores sensitivity on the very next frame.
                if (_darkFrames >= _t.MaxEventFrames)
                {
                    emitted = Close(truncated: true);
                    AdoptBaseline(median);
                }
            }
            else
            {
                Remember(median);

                // The floor this threshold has to clear. Only frames outside an event count: while
                // one is open the baseline is frozen on purpose, so depth there measures the fault
                // rather than the noise.
                if (!_inEvent)
                {
                    if (depth > MaxNormalDepth) MaxNormalDepth = depth;

                    if (coherence > _t.Coherence)
                    {
                        // Nothing but the depth threshold turned this frame away.
                        if (depth > MaxCoherentNormalDepth)
                        {
                            MaxCoherentNormalDepth = depth;
                            MaxCoherentNormalDepthFrame = frame;
                        }
                        if (depth > _t.Depth * 0.5) NormalFramesNearThreshold++;
                    }
                    else if (depth > _t.Depth)
                    {
                        // Deep enough to fire; the tiles disagreed and coherence rejected it.
                        CoherenceSaves++;
                    }
                }

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

            _hasPrevious = false;
            MaxNormalDepth = 0;
            MaxCoherentNormalDepth = 0;
            MaxCoherentNormalDepthFrame = 0;
            NormalFramesNearThreshold = 0;
            CoherenceSaves = 0;
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

        private AnomalyEvent Close(bool truncated = false)
        {
            // Duration is frames times the frame period, not the span between the first and last
            // timestamps: the span leaves out the last frame's own exposure and under-reports a
            // two-frame blank by half. A single-frame event would report zero.
            double period = _framePeriodSec > 0 ? _framePeriodSec : 0;
            var e = new AnomalyEvent(
                _startFrame, _lastDarkFrame, _darkFrames,
                _maxDepth, _maxCoherence,
                _startTime, _darkFrames * period * 1000.0, truncated);

            _inEvent = false;
            _clearFrames = 0;
            return e;
        }

        /// <summary>
        /// Throws away the baseline history and makes <paramref name="median"/> the whole of it, so
        /// the next frame is judged against what the screen is doing now rather than what it was
        /// doing before a capped event began.
        ///
        /// The window is filled rather than emptied: leaving one sample in it would let the next
        /// few frames drag the baseline about, and re-entering the warmup would blind the detector
        /// for the thirty frames it was just unblocked from.
        /// </summary>
        private void AdoptBaseline(double median)
        {
            for (int i = 0; i < _window.Length; i++) _window[i] = median;
            _windowCount = _window.Length;
            _windowNext = 0;
            Baseline = median;
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
            _previous.CopyFrom(grid);
            _hasPrevious = true;
            _previousFrame = grid.FrameNumber;
            _previousTime = timeStampSec;
        }
    }
}
