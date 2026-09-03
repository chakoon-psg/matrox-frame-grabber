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
        /// How much the tiles must agree before a fall is believed. The defence against content
        /// changing -- but only against one kind of it, and the boundary is worth knowing before
        /// this is the knob anyone reaches for.
        ///
        /// It rejects content that moves tiles in different directions, which is most animation:
        /// measured on the load clip, coherence turned away every burst on two channels while the
        /// depth reached 0.39. It cannot reject content that dims the whole region at once. On the
        /// same clip, played twice, one channel each time saw the pattern as a uniform 12% dim at
        /// coherence 1.00 -- and which channel that was changed between runs, so it follows viewing
        /// geometry rather than the camera.
        ///
        /// That is not a gate failure. A brief uniform dim from content is physically the same
        /// measurement as a brief uniform dim from a fault; nothing computed from one frame pair
        /// separates them. What separates them is the depth threshold's height, which is why that
        /// one is per channel.
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
        /// How many frames the fall may take to cover the tiles before it is read as something
        /// crossing the field rather than the surface dimming. 6 frames is about 48 ms at 124.3 fps.
        ///
        /// Measured against known content: the full-field dips of the load clip spread over 1
        /// frame, and its moving content over 37 to 412. Nothing fell in between.
        ///
        /// Neither depth nor coherence can make this distinction. An object crossing the field
        /// darkens the tiles it has reached, and those all move the same way, so coherence reads
        /// 1.00 exactly as a panel switching off does.
        ///
        /// The number is 6 rather than half the gap because of what this can see. Onset is only
        /// tracked once the event is open, and an event opens when the median crosses -- which for
        /// a sweep is around the time it has covered half the tiles. So the spread measured here is
        /// roughly half the real one, and a budget of 6 turns away sweeps that take about 12 frames
        /// or more in total. Against a real dimming that spreads over 0 to 1 frames, that still
        /// leaves six times the margin.
        /// </summary>
        public int MaxOnsetSpreadFrames { get; set; } = 6;

        /// <summary>
        /// Tiles that must have fallen before the spread is trusted enough to reject on. A shallow
        /// event moves few tiles, and a spread measured from two of them says nothing -- so below
        /// this the gate stands aside rather than guessing.
        /// </summary>
        public int MinOnsetTiles { get; set; } = 8;

        /// <summary>
        /// False positives the operator will tolerate per hour on this channel. The rule a proposed
        /// <see cref="Depth"/> is derived from - not something anything watches afterwards.
        /// </summary>
        public double FalsePositiveBudgetPerHour { get; set; } = 1.0;

        /// <summary>
        /// When <see cref="Depth"/> was last set from a measurement, in ISO 8601, or empty if it
        /// never was.
        ///
        /// Recorded because a settings file cannot otherwise be trusted. Six months on, 0.15 in a
        /// file says nothing about whether it came from a calibration run or from somebody trying
        /// numbers - and a calibration that predates a change of lens or lighting is worse than
        /// none, because it looks authoritative.
        /// </summary>
        public string CalibratedAt { get; set; } = string.Empty;

        /// <summary>Frames the calibration judged, and the depth its normal frames reached.</summary>
        public long CalibrationFrames { get; set; }
        public double CalibrationFloor { get; set; }

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
            MaxOnsetSpreadFrames = Clamp(other.MaxOnsetSpreadFrames, 1, 1000000);
            MinOnsetTiles = Clamp(other.MinOnsetTiles, 1, TileGrid.TileCount);
            FalsePositiveBudgetPerHour = Clamp(other.FalsePositiveBudgetPerHour, 0.001, 100000.0);

            // Provenance is taken verbatim: it records where Depth came from, and clamping a record
            // of the past would make it a different record.
            CalibratedAt = other.CalibratedAt ?? string.Empty;
            CalibrationFrames = other.CalibrationFrames;
            CalibrationFloor = other.CalibrationFloor;
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

        /// <summary>
        /// Frames between the first tile falling and the last. Near zero for a surface that dimmed
        /// at once; hundreds for something that crossed the field. Carried on the event so a report
        /// says why it was believed, and so a rejected one can be argued with.
        /// </summary>
        public int OnsetSpreadFrames { get; }

        /// <summary>Tiles that fell far enough to have an onset at all.</summary>
        public int OnsetTiles { get; }

        public AnomalyEvent(long startFrame, long endFrame, int frameCount,
                            double maxDepth, double maxCoherence,
                            double startTimeSec, double durationMs,
                            bool truncated = false,
                            int onsetSpreadFrames = 0, int onsetTiles = 0)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            FrameCount = frameCount;
            MaxDepth = maxDepth;
            MaxCoherence = maxCoherence;
            StartTimeSec = startTimeSec;
            DurationMs = durationMs;
            Truncated = truncated;
            OnsetSpreadFrames = onsetSpreadFrames;
            OnsetTiles = onsetTiles;
        }

        public override string ToString() =>
            $"frame {StartFrame}-{EndFrame} ({FrameCount}), {DurationMs:F1} ms, " +
            $"depth {MaxDepth:F2}, coh {MaxCoherence:F2}, " +
            $"onset {OnsetSpreadFrames}f over {OnsetTiles} tiles" +
            (Truncated ? " (still running - duration is a floor)" : string.Empty);
    }

    /// <summary>
    /// A depth threshold worked out from what a run measured, and the evidence behind it.
    ///
    /// The rule is a rate, not a margin: given a budget of false positives per hour, this is the
    /// lowest depth at which the run's normal frames exceeded it rarely enough to stay inside that
    /// budget. Stated that way because it is the quantity anyone actually cares about, and because
    /// a few frames on the shoulder of a real event cannot move it - which a maximum can, and did.
    /// The same channel measured 0.0028, 0.0089 and 0.0475 across three runs on one rig.
    /// </summary>
    public readonly struct DepthProposal
    {
        /// <summary>The proposed threshold. Zero when the run could not support one.</summary>
        public double Depth { get; }

        /// <summary>
        /// Frames the run judged, which is what its length is measured from. Not the population the
        /// depths came from: that is <see cref="FramesInPopulation"/>, and on a still panel it is a
        /// few per cent of this. Dividing by the wrong one made a five-minute run look like two
        /// seconds and would have proposed a threshold far too high.
        /// </summary>
        public long FramesJudged { get; }

        /// <summary>
        /// Frames the depths were taken from: judged, outside an event and its approach, with the
        /// tiles in agreement. Those are the ones coherence would not have stopped, so they are the
        /// ones the depth threshold has to. A small number here is the coherence gate working.
        /// </summary>
        public long FramesInPopulation { get; }

        /// <summary>Frames still above <see cref="Depth"/> - inside the allowance, by definition.</summary>
        public long FramesAbove { get; }

        /// <summary>Frames the budget allowed over a run of this length.</summary>
        public double AllowedFrames { get; }

        /// <summary>
        /// The tightest budget this run could resolve, per hour. A ten-minute run cannot
        /// demonstrate one false positive an hour: with nothing above a threshold it can only say
        /// the rate is under six an hour. Carried so the proposal is not read as more than it is.
        /// </summary>
        public double MeasurableBudgetPerHour { get; }

        /// <summary>
        /// Whether the run was long enough to propose from. The run, not the population: what has
        /// to be long is the time, and a population of a few hundred frames out of a long run is
        /// the coherence gate doing its job rather than a shortage of data.
        /// </summary>
        public bool IsUsable => FramesJudged >= AnomalyDetector.MinCalibrationFrames && Depth > 0;

        public DepthProposal(double depth, long framesJudged, long framesInPopulation,
                             long framesAbove, double allowedFrames, double measurableBudgetPerHour)
        {
            Depth = depth;
            FramesJudged = framesJudged;
            FramesInPopulation = framesInPopulation;
            FramesAbove = framesAbove;
            AllowedFrames = allowedFrames;
            MeasurableBudgetPerHour = measurableBudgetPerHour;
        }

        public override string ToString() =>
            !IsUsable
                ? $"not enough to propose from ({FramesJudged} frames judged)"
                : $"depth {Depth:0.###} from {FramesJudged} frames judged, "
                + $"{FramesInPopulation} in the population "
                + $"({FramesAbove} above it, {AllowedFrames:0.#} allowed; "
                + $"this run resolves {MeasurableBudgetPerHour:0.#}/hour)";
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

        // Per-tile onset within the open event: the tile's level just before the event began, and
        // the frame at which it first fell past the depth threshold from there.
        // The depth of every normal frame whose tiles agreed, in thousandths. That is exactly the
        // population the depth threshold has to turn away by itself, and 0.001 is finer resolution
        // than any threshold worth choosing.
        private readonly long[] _depthHistogram = new long[DepthBuckets];
        private long _histogramFrames;

        // The buckets the most recent frames went into, so they can be taken back out if an event
        // turns out to have been starting. A fault does not begin between two frames.
        private readonly int[] _recentBuckets = new int[HistogramGuardFrames];
        private int _recentCount;
        private int _recentNext;

        // Frames still to skip after an event closed: the way back up is not normal either.
        private int _histogramSuppress;

        private readonly double[] _entryLevel = new double[TileGrid.TileCount];
        private readonly long[] _tileOnset = new long[TileGrid.TileCount];
        private int _onsetTiles;
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

        /// <summary>Buckets in the depth histogram, one per thousandth of depth.</summary>
        public const int DepthBuckets = 1000;

        /// <summary>
        /// Frames either side of an event kept out of the calibration histogram - about 240 ms at
        /// 124.3 fps.
        ///
        /// Not an arbitrary guard. The frame before an event opens is already partway into the
        /// fall: one measured 0.0994 where everything away from an event sat under 0.011, and that
        /// single frame was what made a reported floor look like it had no margin at all. Leaving
        /// them in and hoping the budget absorbs them does not work either - at one false positive
        /// an hour, a five-minute run allows 0.089 frames, so five outliers set the threshold by
        /// themselves.
        /// </summary>
        public const int HistogramGuardFrames = 30;

        /// <summary>
        /// Frames a run must judge before a proposal is offered - about 80 s at 124.3 fps. Below
        /// that the tail is a handful of frames and the proposal would follow whichever way they
        /// happened to fall.
        /// </summary>
        public const long MinCalibrationFrames = 10000;

        /// <summary>Frames in the histogram a proposal would be computed from.</summary>
        public long HistogramFrames => _histogramFrames;

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
        /// Events that met depth and coherence but whose fall swept across rather than happening at
        /// once. Counted, and the last one kept, because a gate whose rejections leave no trace is
        /// indistinguishable from a rig with nothing wrong.
        /// </summary>
        public long EventsRejectedForSpread { get; private set; }

        /// <summary>The most recent rejected event, or null. Reset when the next one is rejected.</summary>
        public AnomalyEvent? LastRejectedEvent { get; private set; }

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

                    // The level each tile was at before the fall, taken from the previous frame.
                    // Per tile rather than one figure: the whole question is whether the tiles fell
                    // together, and they start from different brightnesses.
                    for (int i = 0; i < TileGrid.TileCount; i++)
                    {
                        _entryLevel[i] = _hasPrevious && _previous.IsPopulated(i) ? _previous.Mean(i) : 0.0;
                        _tileOnset[i] = -1;
                    }
                    _onsetTiles = 0;

                    // Those frames were the approach to this, not a description of normal.
                    ForgetRecentHistogramFrames();
                }

                // When each tile crossed the same threshold the event was judged by. Online, so no
                // per-frame history is kept: one pass over 64 tiles.
                for (int i = 0; i < TileGrid.TileCount; i++)
                {
                    if (_tileOnset[i] >= 0 || _entryLevel[i] <= 0 || !grid.IsPopulated(i))
                        continue;
                    if (grid.Mean(i) <= _entryLevel[i] * (1.0 - _t.Depth))
                    {
                        _tileOnset[i] = frame;
                        _onsetTiles++;
                    }
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
                    emitted = Judge(Close(truncated: true));
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

                        if (_histogramSuppress > 0)
                        {
                            _histogramSuppress--;      // still climbing out of the last event
                        }
                        else
                        {
                            int bucket = (int)(depth * DepthBuckets);
                            if (bucket < 0) bucket = 0;
                            if (bucket >= DepthBuckets) bucket = DepthBuckets - 1;
                            _depthHistogram[bucket]++;
                            _histogramFrames++;

                            _recentBuckets[_recentNext] = bucket;
                            _recentNext = (_recentNext + 1) % HistogramGuardFrames;
                            if (_recentCount < HistogramGuardFrames) _recentCount++;
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
                    emitted = Judge(Close());
            }

            _observed++;
            AdoptAsReference(grid, timeStampSec);
            return emitted;
        }

        /// <summary>
        /// Closes an open event, for when acquisition stops. Without it, a fault still running when
        /// the grab ends is never reported at all.
        /// </summary>
        public AnomalyEvent? Flush() => _inEvent ? Judge(Close()) : (AnomalyEvent?)null;

        /// <summary>
        /// Takes the most recently counted frames back out of the histogram, for when an event
        /// turns out to have been beginning during them.
        /// </summary>
        private void ForgetRecentHistogramFrames()
        {
            for (int n = 0; n < _recentCount; n++)
            {
                int slot = (_recentNext - 1 - n + HistogramGuardFrames * 2) % HistogramGuardFrames;
                int bucket = _recentBuckets[slot];
                if (_depthHistogram[bucket] > 0)
                {
                    _depthHistogram[bucket]--;
                    _histogramFrames--;
                }
            }
            _recentCount = 0;
            _recentNext = 0;
        }

        /// <summary>
        /// The lowest depth threshold this run supports for a given budget of false positives per
        /// hour, found by walking down the histogram until the tail no longer fits the allowance.
        ///
        /// <paramref name="fps"/> turns frames into hours, so it has to be the rate the run
        /// achieved rather than a nominal one: the same frame count is a different length of time
        /// at a different exposure.
        ///
        /// A run too short for the budget is not an error. With nothing above a threshold, ten
        /// minutes bounds the rate at six an hour and no tighter, and the proposal carries that
        /// bound rather than pretending to the budget it was asked for.
        /// </summary>
        public DepthProposal Propose(double budgetPerHour, double fps)
        {
            if (_histogramFrames <= 0 || fps <= 0 || budgetPerHour <= 0)
                return new DepthProposal(0, _observed, _histogramFrames, 0, 0, 0);

            // The run's length, from every frame it judged. Not from the histogram: that holds only
            // the frames the coherence gate let through, a few per cent on a still panel, and using
            // it made a five-minute run look like two seconds.
            double seconds = _observed / fps;
            double allowed = budgetPerHour * seconds / 3600.0;
            double resolvable = 3600.0 / seconds;

            // Down from the top. The first bucket whose tail no longer fits is one below the
            // answer, so the threshold is that bucket's upper edge.
            long tail = 0;
            int bucket = DepthBuckets - 1;
            for (; bucket >= 0; bucket--)
            {
                long next = tail + _depthHistogram[bucket];
                if (next > allowed)
                    break;
                tail = next;
            }

            double depth = (bucket + 2) / (double)DepthBuckets;
            if (depth > 1.0) depth = 1.0;

            return new DepthProposal(depth, _observed, _histogramFrames, tail, allowed, resolvable);
        }

        /// <summary>
        /// Lets a closed event through, or turns it away because its fall swept across the tiles
        /// rather than covering them at once.
        ///
        /// This is the only gate that cannot sit at entry: the spread is not known until the fall
        /// has finished spreading. Putting it here costs nothing, because an event is already
        /// emitted late - after the debounce.
        ///
        /// It stands aside when too few tiles fell. A shallow event moves few of them, and a spread
        /// measured from two tiles is not a measurement.
        /// </summary>
        private AnomalyEvent? Judge(AnomalyEvent candidate)
        {
            if (candidate.OnsetTiles < _t.MinOnsetTiles ||
                candidate.OnsetSpreadFrames <= _t.MaxOnsetSpreadFrames)
                return candidate;

            EventsRejectedForSpread++;
            LastRejectedEvent = candidate;
            return null;
        }

        public void Reset()
        {
            Array.Clear(_window, 0, _window.Length);
            _windowCount = 0;
            _windowNext = 0;
            _observed = 0;
            Baseline = 0;

            _hasPrevious = false;
            _onsetTiles = 0;
            EventsRejectedForSpread = 0;
            LastRejectedEvent = null;
            for (int i = 0; i < TileGrid.TileCount; i++) _tileOnset[i] = -1;
            Array.Clear(_depthHistogram, 0, _depthHistogram.Length);
            _histogramFrames = 0;
            _recentCount = 0;
            _recentNext = 0;
            _histogramSuppress = 0;
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

        /// <summary>
        /// Frames between the first tile falling and the last, over the tiles that fell at all.
        /// Zero when fewer than two did.
        /// </summary>
        private int OnsetSpread()
        {
            long first = long.MaxValue, last = long.MinValue;
            int seen = 0;
            for (int i = 0; i < TileGrid.TileCount; i++)
            {
                if (_tileOnset[i] < 0) continue;
                if (_tileOnset[i] < first) first = _tileOnset[i];
                if (_tileOnset[i] > last) last = _tileOnset[i];
                seen++;
            }
            return seen < 2 ? 0 : (int)(last - first);
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
                _startTime, _darkFrames * period * 1000.0, truncated,
                OnsetSpread(), _onsetTiles);

            _inEvent = false;
            _clearFrames = 0;
            _histogramSuppress = HistogramGuardFrames;
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
