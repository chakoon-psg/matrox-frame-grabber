using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Decides when the next frame should be read, and nothing else.
    ///
    /// Split out from the reading because **this is the part that can be wrong without anything
    /// looking wrong**: a patrol that fires twice as often as intended still produces plausible
    /// findings, and the only symptom is load nobody attributed to it. Here it is arithmetic on
    /// frame numbers and timestamps, with tests.
    ///
    /// **Frames, not a timer.** A `DispatcherTimer` would run on the UI thread, and the frame it
    /// happened to grab would be whatever the display buffer held - which, as this repo learned
    /// the hard way, is the previous run's last frame when acquisition has stopped. Counting the
    /// frames that actually arrived ties the patrol to acquisition instead.
    ///
    /// The name is `Patrol` because `Sweep` is taken by `PwmSweep`, and a sweep is a measurement
    /// that walks a parameter - this walks time.
    /// </summary>
    public sealed class ScreenPatrol
    {
        private double _lastAtSec = double.NegativeInfinity;
        private long _lastFrame = long.MinValue;

        public ScreenPatrol(double everySeconds)
        {
            EverySeconds = everySeconds <= 0.0 ? 0.0 : everySeconds;
        }

        /// <summary>How often a frame should be inspected. 0 switches the patrol off.</summary>
        public double EverySeconds { get; }

        /// <summary>Whether the patrol does anything at all.</summary>
        public bool Enabled => EverySeconds > 0.0;

        /// <summary>Frames handed over for inspection so far.</summary>
        public long Taken { get; private set; }

        /// <summary>The frame number of the last one taken, or 0.</summary>
        public long LastFrame => _lastFrame == long.MinValue ? 0 : _lastFrame;

        /// <summary>
        /// Whether this frame is the one to inspect.
        ///
        /// The first frame after a start is always taken: a run that ends before the first
        /// interval elapses would otherwise inspect nothing and look like a run where the
        /// inspector was off.
        ///
        /// A timestamp that goes backwards - which a board reset does - is treated as a fresh
        /// start rather than as a very long interval, so the patrol does not then fire on every
        /// frame until the clock catches up.
        /// </summary>
        public bool ShouldInspect(long frameNumber, double timeStampSec)
        {
            if (!Enabled) return false;

            if (_lastAtSec == double.NegativeInfinity || timeStampSec < _lastAtSec)
            {
                _lastAtSec = timeStampSec;
                _lastFrame = frameNumber;
                Taken++;
                return true;
            }

            if (timeStampSec - _lastAtSec < EverySeconds) return false;

            _lastAtSec = timeStampSec;
            _lastFrame = frameNumber;
            Taken++;
            return true;
        }

        /// <summary>Forgets the last time, so the next frame is taken. Called at grab start.</summary>
        public void Reset()
        {
            _lastAtSec = double.NegativeInfinity;
            _lastFrame = long.MinValue;
            Taken = 0;
        }

        public override string ToString() =>
            Enabled ? $"patrol every {EverySeconds:0.##} s ({Taken} taken)" : "patrol off";
    }
}
