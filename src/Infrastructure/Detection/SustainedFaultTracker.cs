using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The state machine a sustained kind needs, without the judgement of what it is looking at.
    ///
    /// Blackout and Washout ask different questions of a frame - one is dark and flat, the other
    /// is bright and featureless - but what they do with the answer is identical, and it is not
    /// the same thing an event kind does:
    ///
    ///   - **a dwell on each side**, because a sustained fault is defined by staying, and one
    ///     frame of either is what a legitimate cut looks like;
    ///   - **hysteresis**, so a panel sitting on the line does not report a fault every few
    ///     frames - `CONTEXT.md` makes that the dividing line between a sustained kind and an
    ///     event one;
    ///   - **one report**, at the moment the dwell confirms, which is also while the evidence ring
    ///     buffer still holds the transition;
    ///   - **no cap**, because neither holds a baseline that a fault could erase - which is the
    ///     whole reason `AnomalyDetector` needs one.
    ///
    /// Two details here were bought with hardware time and are easy to lose in a rewrite. Nothing
    /// confirms before <see cref="RateKnown"/>: a dwell in milliseconds cannot be checked against
    /// a rate of zero, and the start of a grab is exactly when the picture can legitimately be
    /// black. And a hole in the frame numbering restarts the dwell: the level would still read
    /// across it, but a dwell is a claim about how long something held and frames nobody saw
    /// cannot support it.
    /// </summary>
    public sealed class SustainedFaultTracker
    {
        private readonly AnomalyKind _kind;

        private int _faultRun;
        private int _clearRun;
        private bool _inFault;
        private bool _reported;

        private long _startFrame, _lastFrame;
        private long _previousFrame = -1;
        private double _startTime, _previousTime;
        private double _framePeriodSec;
        private double _worstDeviation;

        public SustainedFaultTracker(AnomalyKind kind)
        {
            _kind = kind;
        }

        /// <summary>Whether the fault is running right now. A sustained kind is a state.</summary>
        public bool InFault => _inFault;

        /// <summary>
        /// Whether a frame period has been measured, which takes two consecutive frames.
        ///
        /// Nothing confirms before this is true.
        /// </summary>
        public bool RateKnown => _framePeriodSec > 0.0;

        /// <summary>Frames the current fault has run, or 0.</summary>
        public int RunningFrames => _inFault ? (int)(_lastFrame - _startFrame + 1) : 0;

        /// <summary>How long it has run, in ms, or 0.</summary>
        public double RunningMs => _inFault ? RunningFrames * _framePeriodSec * 1000.0 : 0.0;

        /// <summary>Frames skipped because the numbering jumped, each of which restarted the dwell.</summary>
        public long FramesSkippedForGaps { get; private set; }

        /// <summary>True on the frame the fault was confirmed, so stills can be kept.</summary>
        public bool EnteredThisFrame { get; private set; }

        /// <summary>True on the first frame the picture is back.</summary>
        public bool RecoveredThisFrame { get; private set; }

        /// <summary>Seconds per frame as measured, or 0 before two frames have arrived.</summary>
        public double FramePeriodSec => _framePeriodSec;

        /// <summary>Frames per second as measured, or 0.</summary>
        public double Fps => _framePeriodSec > 0.0 ? 1.0 / _framePeriodSec : 0.0;

        /// <summary>
        /// Advances one frame. Returns the event on the frame the dwell confirms, and null on
        /// every other frame - including every frame of a fault already reported.
        ///
        /// <paramref name="faulted"/> and <paramref name="cleared"/> are the caller's judgement of
        /// this frame and are deliberately not opposites: the gap between them is the hysteresis,
        /// and a frame that is neither changes nothing.
        /// </summary>
        public AnomalyEvent? Observe(long frame, double timeStampSec,
                                     bool faulted, bool cleared,
                                     double deviation, double enterMs, double recoverMs)
        {
            EnteredThisFrame = false;
            RecoveredThisFrame = false;

            if (_previousFrame >= 0 && frame != _previousFrame + 1)
            {
                FramesSkippedForGaps++;
                _faultRun = 0;
                _clearRun = 0;
                _previousFrame = frame;
                _previousTime = timeStampSec;
                return null;
            }

            if (_previousFrame >= 0 && timeStampSec > _previousTime)
                _framePeriodSec = timeStampSec - _previousTime;
            _previousFrame = frame;
            _previousTime = timeStampSec;

            AnomalyEvent? emitted = null;

            if (faulted)
            {
                _clearRun = 0;
                if (_faultRun == 0)
                {
                    _startFrame = frame;
                    _startTime = timeStampSec;
                    _worstDeviation = deviation;
                }
                _faultRun++;
                _lastFrame = frame;
                if (deviation > _worstDeviation) _worstDeviation = deviation;

                if (!_reported && RateKnown && _faultRun >= Frames(enterMs))
                {
                    _inFault = true;
                    _reported = true;
                    EnteredThisFrame = true;
                    emitted = Emit();
                }
            }
            else
            {
                _faultRun = 0;
                if (_inFault) _lastFrame = frame;

                if (cleared)
                {
                    _clearRun++;
                    if (RateKnown && _clearRun >= Frames(recoverMs))
                    {
                        if (_inFault) RecoveredThisFrame = true;
                        _inFault = false;
                        _reported = false;
                        _clearRun = 0;
                    }
                }
                else
                {
                    // Between the two levels. That gap is the hysteresis and sitting in it
                    // changes nothing.
                    _clearRun = 0;
                }
            }

            return emitted;
        }

        /// <summary>
        /// Reports a fault that was running but never confirmed, for when acquisition stops.
        ///
        /// Without it a panel that failed four frames before the run ended is lost entirely: the
        /// dwell never completed and nothing was emitted.
        /// </summary>
        public AnomalyEvent? Flush()
        {
            if (_reported || _faultRun < 1) return null;
            _inFault = true;
            _reported = true;
            return Emit(truncated: true);
        }

        public void Reset()
        {
            _faultRun = 0;
            _clearRun = 0;
            _inFault = false;
            _reported = false;
            _previousFrame = -1;
            _previousTime = 0.0;
            _framePeriodSec = 0.0;
            _worstDeviation = 0.0;
            FramesSkippedForGaps = 0;
            EnteredThisFrame = false;
            RecoveredThisFrame = false;
        }

        private int Frames(double ms)
        {
            double f = ms * Fps / 1000.0;
            if (double.IsNaN(f) || f < 1.0) return 1;
            return f > int.MaxValue ? int.MaxValue : (int)Math.Round(f);
        }

        private AnomalyEvent Emit(bool truncated = false)
        {
            int frames = (int)(_lastFrame - _startFrame + 1);
            double period = _framePeriodSec > 0.0 ? _framePeriodSec : 0.0;

            // Coherence 1.0 rather than 0. No delta is measured here, and 0 would read as "the
            // tiles disagreed" - the opposite of the finding, which is that they all agree.
            return new AnomalyEvent(
                _startFrame, _lastFrame, frames,
                _worstDeviation, 1.0,
                _startTime, frames * period * 1000.0,
                truncated,
                onsetSpreadFrames: 0, onsetTiles: 0,
                kind: _kind);
        }
    }
}
