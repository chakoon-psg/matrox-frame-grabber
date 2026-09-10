using System;
using System.Collections.Generic;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// How one kind of anomaly should be turned into evidence.
    ///
    /// A clip per confirmed event is right for Dropout and wrong for everything else. Events are
    /// closed at the duration cap - 2010 ms - so a fault that stays produces one every two seconds:
    /// a blackout lasting a minute would ask for thirty clips of the same thing, and flicker asks
    /// for several a second. What differs between the kinds is not the detector, it is how many
    /// occurrences one report should cover.
    /// </summary>
    public readonly struct AnomalyClipPolicy
    {
        /// <summary>
        /// Events this close to the previous one are the same occurrence, and extend its window
        /// rather than starting a new clip. Sized above the duration cap for a kind that persists,
        /// so consecutive truncated events fold together.
        /// </summary>
        public readonly double MergeGapSec;

        /// <summary>
        /// After a clip is cut, how long before this kind may produce another. Zero means every
        /// occurrence gets one, which is what a dropout wants: they are the thing being counted.
        /// </summary>
        public readonly double CooldownSec;

        /// <summary>Whether to keep lossless stills. A flip needs one frame; flicker needs none.</summary>
        public readonly bool Stills;

        /// <summary>
        /// Whether the tile-history CSV is the evidence rather than the pictures. True for flicker,
        /// where what matters is the waveform over a second, not four frames from it.
        /// </summary>
        public readonly bool Waveform;

        public AnomalyClipPolicy(double mergeGapSec, double cooldownSec, bool stills, bool waveform)
        {
            MergeGapSec = mergeGapSec;
            CooldownSec = cooldownSec;
            Stills = stills;
            Waveform = waveform;
        }

        /// <summary>
        /// The policy for a kind.
        ///
        /// Code rather than settings: these follow from what each fault looks like, not from the
        /// site. A person tunes how much of the event to keep - that is one number in settings -
        /// while whether a sustained fault is one report or thirty is a property of the fault.
        /// </summary>
        public static AnomalyClipPolicy For(AnomalyKind kind)
        {
            switch (kind)
            {
                // Each dropout is an occurrence and the count is the point. Merge only what the
                // debounce could split - 1 s is comfortably above the 161 ms debounce and well
                // under the gap between the dips measured on these panels.
                case AnomalyKind.Dropout:
                    return new AnomalyClipPolicy(1.0, 0.0, stills: true, waveform: false);

                // Sustained. The cap closes an event every 2.01 s while the picture is still gone,
                // so merge above that and then hold off a minute: one clip per minute of an
                // ongoing blackout says as much as sixty would.
                case AnomalyKind.Blackout:
                case AnomalyKind.Washout:
                    return new AnomalyClipPolicy(2.5, 60.0, stills: true, waveform: false);

                // Periodic, and the evidence is the shape over time. Five minutes between clips,
                // and no stills: four frames out of an oscillation say nothing the tile history
                // does not say better.
                case AnomalyKind.Flicker:
                    return new AnomalyClipPolicy(2.5, 300.0, stills: false, waveform: true);

                // A single frame shows it, and it does not come and go within a second.
                case AnomalyKind.Flip:
                    return new AnomalyClipPolicy(5.0, 60.0, stills: true, waveform: false);

                default:
                    return new AnomalyClipPolicy(1.0, 0.0, stills: true, waveform: false);
            }
        }
    }

    /// <summary>How much of an event to keep, and how long to wait before it can be read.</summary>
    public readonly struct AnomalyClipSettings
    {
        /// <summary>Seconds kept before the event started and after it ended.</summary>
        public readonly double AroundSec;

        /// <summary>
        /// How long after the window before the files holding it are closed and readable. One
        /// segment length: the muxer does not close the file it is writing.
        /// </summary>
        public readonly double SegmentCloseSec;

        public AnomalyClipSettings(double aroundSec, double segmentCloseSec)
        {
            AroundSec = aroundSec < 0.0 ? 0.0 : aroundSec;
            SegmentCloseSec = segmentCloseSec < 0.0 ? 0.0 : segmentCloseSec;
        }

        /// <summary>The retention a ring needs to still hold the start of a window this wide.</summary>
        public double RequiredRingSec(double maxEventSec) =>
            AroundSec * 2.0 + maxEventSec + SegmentCloseSec;
    }

    /// <summary>One clip to cut, in board time.</summary>
    public readonly struct ClipRequest
    {
        public readonly AnomalyKind Kind;

        /// <summary>Window start, board seconds.</summary>
        public readonly double FromSec;

        /// <summary>Window end, board seconds.</summary>
        public readonly double ToSec;

        /// <summary>Board time at which the files covering <see cref="ToSec"/> are readable.</summary>
        public readonly double DueSec;

        public readonly long StartFrame;
        public readonly long EndFrame;

        /// <summary>Events this clip covers. Above one means occurrences were folded together.</summary>
        public readonly int Occurrences;

        /// <summary>Whether any event in it was closed by the duration cap.</summary>
        public readonly bool Truncated;

        /// <summary>The deepest deviation across the events covered.</summary>
        public readonly double MaxDeviation;

        public ClipRequest(AnomalyKind kind, double fromSec, double toSec, double dueSec,
                           long startFrame, long endFrame, int occurrences, bool truncated,
                           double maxDeviation)
        {
            Kind = kind;
            FromSec = fromSec;
            ToSec = toSec;
            DueSec = dueSec;
            StartFrame = startFrame;
            EndFrame = endFrame;
            Occurrences = occurrences;
            Truncated = truncated;
            MaxDeviation = maxDeviation;
        }

        public double LengthSec => ToSec - FromSec;

        public override string ToString() =>
            $"{Kind} clip {FromSec:F3}-{ToSec:F3} s ({LengthSec:F1} s), frames {StartFrame}-{EndFrame}, "
          + $"{Occurrences} occurrence(s), {AnomalyCatalog.DeviationWord(Kind)} {MaxDeviation:F2}"
          + (Truncated ? ", truncated" : string.Empty);
    }

    /// <summary>
    /// Decides which confirmed events become clips, and when each clip can be cut.
    ///
    /// Two jobs, and the first is why this exists. Events arrive faster than they should become
    /// files: a fault that stays produces one every 2.01 s because that is where the duration cap
    /// closes them, and asking for a clip each would fill the disk with the same ten seconds. So
    /// consecutive events of a kind fold into one occurrence, and after a clip is cut that kind
    /// waits out a cooldown.
    ///
    /// The second is that a clip cannot be cut when the event is reported. At the moment a report
    /// arrives the "after" seconds have not happened yet, and the files holding them are still open
    /// - so each clip carries a due time and is taken later.
    ///
    /// Board time throughout, and <paramref name="nowSec"/> in <see cref="TryTakeDue"/> must be the
    /// board stamp of the newest frame. That is the same clock the events carry, and it stops
    /// maturing when the grab stops - which is correct, because no more frames are coming.
    /// </summary>
    public sealed class ClipScheduler
    {
        /// <summary>
        /// Pending clips held at once, across all kinds. A bound rather than a queue that grows:
        /// the point of this class is that a storm produces few files, so a storm must not instead
        /// produce a large amount of state.
        /// </summary>
        public const int MaxPending = 8;

        private sealed class Pending
        {
            public AnomalyKind Kind;
            public double FromSec, ToSec;
            public long StartFrame, EndFrame;
            public int Occurrences;
            public bool Truncated;
            public double MaxDeviation;
        }

        private readonly AnomalyClipSettings _settings;
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly double[] _lastEmittedEnd = new double[AnomalyCatalog.Count];
        private readonly bool[] _everEmitted = new bool[AnomalyCatalog.Count];

        public ClipScheduler(AnomalyClipSettings settings) { _settings = settings; }

        /// <summary>Events folded into a clip that was already pending.</summary>
        public long Merged { get; private set; }

        /// <summary>Events that produced no clip because their kind was still in cooldown.</summary>
        public long Suppressed { get; private set; }

        /// <summary>Events dropped because too many clips were already pending.</summary>
        public long Overflowed { get; private set; }

        /// <summary>Clips handed out.</summary>
        public long Emitted { get; private set; }

        /// <summary>Clips waiting for their due time.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Offers a confirmed event. Returns true when it started or extended a clip, false when
        /// it was suppressed or overflowed - the counters say which.
        /// </summary>
        public bool Offer(AnomalyEvent e)
        {
            AnomalyClipPolicy p = AnomalyClipPolicy.For(e.Kind);
            double eventEnd = e.StartTimeSec + e.DurationMs / 1000.0;

            // Extend a pending clip of the same kind when this event is close enough behind it.
            // Compared against the *event* end rather than the padded window end, so the merge gap
            // means what it says regardless of how much padding is configured.
            foreach (Pending q in _pending)
            {
                if (q.Kind != e.Kind) continue;
                double gap = e.StartTimeSec - (q.ToSec - _settings.AroundSec);
                if (gap > p.MergeGapSec || gap < -p.MergeGapSec) continue;

                if (eventEnd + _settings.AroundSec > q.ToSec)
                {
                    q.ToSec = eventEnd + _settings.AroundSec;
                    q.EndFrame = e.EndFrame;
                }
                q.Occurrences++;
                q.Truncated |= e.Truncated;
                if (e.MaxDeviation > q.MaxDeviation) q.MaxDeviation = e.MaxDeviation;
                Merged++;
                return true;
            }

            int k = AnomalyCatalog.Index(e.Kind);
            if (_everEmitted[k] && e.StartTimeSec - _lastEmittedEnd[k] < p.CooldownSec)
            {
                Suppressed++;
                return false;
            }

            if (_pending.Count >= MaxPending)
            {
                Overflowed++;
                return false;
            }

            _pending.Add(new Pending
            {
                Kind = e.Kind,
                FromSec = Math.Max(0.0, e.StartTimeSec - _settings.AroundSec),
                ToSec = eventEnd + _settings.AroundSec,
                StartFrame = e.StartFrame,
                EndFrame = e.EndFrame,
                Occurrences = 1,
                Truncated = e.Truncated,
                MaxDeviation = e.MaxDeviation,
            });
            return true;
        }

        /// <summary>
        /// Takes the oldest clip whose window has passed and whose files have closed. Call until it
        /// returns false.
        /// </summary>
        public bool TryTakeDue(double nowSec, out ClipRequest request)
        {
            int best = -1;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (nowSec < Due(_pending[i])) continue;
                if (best < 0 || _pending[i].FromSec < _pending[best].FromSec) best = i;
            }
            if (best < 0) { request = default; return false; }

            Pending q = _pending[best];
            _pending.RemoveAt(best);

            int k = AnomalyCatalog.Index(q.Kind);
            _lastEmittedEnd[k] = q.ToSec - _settings.AroundSec;   // the event's end, not the padding
            _everEmitted[k] = true;
            Emitted++;

            request = new ClipRequest(q.Kind, q.FromSec, q.ToSec, Due(q),
                                      q.StartFrame, q.EndFrame, q.Occurrences,
                                      q.Truncated, q.MaxDeviation);
            return true;
        }

        /// <summary>
        /// Hands out every pending clip regardless of its due time, for a grab that is stopping.
        ///
        /// The window will be short of its "after" seconds, because those frames never arrived -
        /// which is the truth about a fault that was still running when the run ended, and better
        /// than discarding the evidence for not being complete.
        /// </summary>
        public IEnumerable<ClipRequest> Flush()
        {
            var all = new List<ClipRequest>(_pending.Count);
            foreach (Pending q in _pending)
                all.Add(new ClipRequest(q.Kind, q.FromSec, q.ToSec, Due(q),
                                        q.StartFrame, q.EndFrame, q.Occurrences,
                                        q.Truncated, q.MaxDeviation));
            _pending.Clear();
            Emitted += all.Count;
            return all;
        }

        public void Reset()
        {
            _pending.Clear();
            Array.Clear(_lastEmittedEnd, 0, _lastEmittedEnd.Length);
            Array.Clear(_everEmitted, 0, _everEmitted.Length);
            Merged = Suppressed = Overflowed = Emitted = 0;
        }

        private double Due(Pending q) => q.ToSec + _settings.SegmentCloseSec;
    }
}
