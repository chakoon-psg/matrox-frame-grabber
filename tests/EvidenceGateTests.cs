using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The gate that keeps three channels from dumping evidence at the same instant.
    ///
    /// It exists because of one measurement: one camera writing 33 lossless windows over 120 s
    /// cost 3/0/2 frames, and three cameras writing at once cost 232/238/226 and dropped the rate
    /// to 57-89 fps. A dump is 660 MB; three of them ask the disk for 2 GB/s.
    /// </summary>
    public class EvidenceGateTests
    {
        [Fact]
        public void One_dump_goes_straight_through()
        {
            var gate = new EvidenceGate();

            Assert.True(gate.Enter(1000, out double waited));
            gate.Exit();

            Assert.True(waited < 0.05, $"an uncontended gate waited {waited:F3} s");
            Assert.Equal(0, gate.Serialised);      // nothing was serialised, so nothing is reported
            Assert.Null(gate.Summary());
        }

        [Fact]
        public async Task The_second_dump_waits_for_the_first()
        {
            var gate = new EvidenceGate();
            Assert.True(gate.Enter(1000, out _));

            var second = Task.Run(() =>
            {
                bool got = gate.Enter(5000, out double w);
                if (got) gate.Exit();
                return (got, w);
            });

            Thread.Sleep(200);
            Assert.Equal(1, gate.Waiting);         // visible while it queues, for the run summary
            gate.Exit();

            (bool got, double waited) = await second;
            Assert.True(got);
            Assert.True(waited >= 0.15, $"the second dump waited only {waited:F3} s");
            Assert.Equal(1, gate.Serialised);
            Assert.Contains("1 dump(s) waited", gate.Summary());
        }

        /// <summary>
        /// A dump cannot legitimately hold the gate for the timeout, so giving up is the right
        /// answer - piling up behind a stuck ffmpeg would cost the next window too.
        /// </summary>
        [Fact]
        public void A_dump_gives_up_rather_than_queueing_behind_something_stuck()
        {
            var gate = new EvidenceGate();
            Assert.True(gate.Enter(1000, out _));

            Assert.False(gate.Enter(120, out double waited));

            Assert.True(waited >= 0.1, $"it gave up after only {waited:F3} s");
            Assert.Equal(0, gate.Serialised);      // it never got in, so it is not a serialised dump
            gate.Exit();
        }

        /// <summary>Three channels, three dumps, and never two inside at once.</summary>
        [Fact]
        public async Task Three_channels_never_overlap()
        {
            var gate = new EvidenceGate();
            int inside = 0, worstInside = 0;
            var done = new List<Task>();

            for (int i = 0; i < 3; i++)
                done.Add(Task.Run(() =>
                {
                    Assert.True(gate.Enter(10000, out _));
                    int now = Interlocked.Increment(ref inside);
                    if (now > worstInside) worstInside = now;
                    Thread.Sleep(60);
                    Interlocked.Decrement(ref inside);
                    gate.Exit();
                }));

            await Task.WhenAll(done);

            Assert.Equal(1, worstInside);
            Assert.Equal(0, gate.Waiting);
            Assert.True(gate.Serialised >= 1, "two of the three had to wait");
        }

        /// <summary>The longest wait is what sizes the slack the ring has to carry.</summary>
        [Fact]
        public async Task It_remembers_the_longest_wait()
        {
            var gate = new EvidenceGate();
            Assert.True(gate.Enter(1000, out _));

            var queued = Task.Run(() => { gate.Enter(5000, out _); gate.Exit(); });
            Thread.Sleep(250);
            gate.Exit();
            await queued;

            Assert.True(gate.LongestWaitSec >= 0.2,
                        $"longest wait recorded as {gate.LongestWaitSec:F3} s");
        }

        [Fact]
        public void A_gate_that_admits_two_admits_two()
        {
            var gate = new EvidenceGate(concurrent: 2);

            Assert.True(gate.Enter(500, out _));
            Assert.True(gate.Enter(500, out _));
            Assert.False(gate.Enter(120, out _));

            gate.Exit();
            gate.Exit();
            Assert.Equal(2, gate.Concurrent);
        }

        [Fact]
        public void The_shared_gate_lets_exactly_one_through()
        {
            Assert.Equal(1, EvidenceGate.Shared.Concurrent);
        }

        [Fact]
        public void A_gate_has_to_admit_somebody()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceGate(0));
        }
    }
}
