using System;
using System.Diagnostics;
using System.Threading;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Lets one evidence dump run at a time across every channel.
    ///
    /// A dump is 660 MB at +-3 s. One of them is free - measured 3/0/2 frames lost over 120 s
    /// while a camera wrote 33 of them. Three at once ask the disk for 2 GB/s, and what gives way
    /// is the acquisition: 232/238/226 lost and the rate down to 57-89 fps. The faults have to be
    /// simultaneous for that, which a reliability test makes unlikely but not impossible - a power
    /// glitch on the bench hits all three DUTs at the same instant.
    ///
    /// Waiting costs nothing that matters. The ring buffer is 12.1 s and one dump may span 8.01 s
    /// of it, so there are about 4 s of slack, and a dump takes 0.6 to 1.5 s. A channel queued
    /// behind two others is still inside that. If it ever is not, the window is resolved AFTER the
    /// wait, so what a late dump loses is the head - the oldest end, furthest from the fault -
    /// and it says so rather than writing a file that quietly starts late.
    /// </summary>
    public sealed class EvidenceGate
    {
        private readonly SemaphoreSlim _gate;
        private int _waiting;

        public EvidenceGate(int concurrent = 1)
        {
            if (concurrent < 1) throw new ArgumentOutOfRangeException(nameof(concurrent));
            Concurrent = concurrent;
            _gate = new SemaphoreSlim(concurrent, concurrent);
        }

        /// <summary>The one every channel shares. Process-wide, which is as wide as the disk is.</summary>
        public static EvidenceGate Shared { get; } = new EvidenceGate(1);

        /// <summary>How many dumps may run at once.</summary>
        public int Concurrent { get; }

        /// <summary>Channels queued behind the one writing, right now.</summary>
        public int Waiting => Volatile.Read(ref _waiting);

        /// <summary>Dumps that had to wait for another channel. 0 means the gate never bit.</summary>
        public long Serialised { get; private set; }

        /// <summary>The longest any dump waited, in seconds. For the run summary.</summary>
        public double LongestWaitSec { get; private set; }

        /// <summary>
        /// Takes the gate, or gives up after <paramref name="timeoutMs"/>.
        ///
        /// False means something is stuck - a dump cannot legitimately hold this for that long -
        /// so the caller logs and drops the window rather than piling up behind it.
        /// </summary>
        public bool Enter(int timeoutMs, out double waitedSec)
        {
            Interlocked.Increment(ref _waiting);
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                bool got = _gate.Wait(timeoutMs);
                waitedSec = (double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency;
                if (got && waitedSec > 0.05)
                {
                    Serialised++;
                    if (waitedSec > LongestWaitSec) LongestWaitSec = waitedSec;
                }
                return got;
            }
            finally { Interlocked.Decrement(ref _waiting); }
        }

        /// <summary>Gives it back. Exactly once per <see cref="Enter"/> that returned true.</summary>
        public void Exit() => _gate.Release();

        /// <summary>A line for the run summary, or null when the gate never made anything wait.</summary>
        public string Summary() =>
            Serialised < 1 ? null
            : $"evidence serialised - {Serialised} dump(s) waited for another channel, "
            + $"longest {LongestWaitSec:F2} s";
    }
}
