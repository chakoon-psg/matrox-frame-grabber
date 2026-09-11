using System;
using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The slot lookup, which stopped being a scan.
    ///
    /// It was 1456 comparisons run twice for every frame a dump writes - about two million a
    /// second inside the lock the acquisition thread needs 120 times a second. A binary search
    /// over the ring's rotated order does it in eleven, and the rotation is where the bugs live:
    /// before the ring fills, once it exactly fills, after it wraps, and while a slot is being
    /// written. Those are the cases here.
    /// </summary>
    public class EvidenceRingLookupTests
    {
        private const int Bpf = 8;

        private static byte[] Frame(byte fill)
        {
            var f = new byte[Bpf];
            for (int i = 0; i < Bpf; i++) f[i] = fill;
            return f;
        }

        /// <summary>Puts <paramref name="n"/> frames in, numbered from <paramref name="from"/>.</summary>
        private static EvidenceRing Filled(int capacity, int n, long from = 0, int step = 1)
        {
            var r = new EvidenceRing(capacity, Bpf);
            for (int i = 0; i < n; i++)
                r.Add(Frame((byte)(i & 0xFF)), from + (long)i * step, 100.0 + i * 0.01);
            return r;
        }

        // ----- before it fills -----

        [Fact]
        public void A_partly_filled_ring_finds_what_it_has_and_nothing_else()
        {
            var r = Filled(capacity: 16, n: 5, from: 1000);

            for (long n = 1000; n < 1005; n++)
                Assert.True(r.StillHolds(n), $"frame {n} should be held");

            Assert.False(r.StillHolds(999));
            Assert.False(r.StillHolds(1005));
            Assert.False(r.StillHolds(0));
            Assert.Equal(5, r.Written);
        }

        [Fact]
        public void An_empty_ring_holds_nothing()
        {
            var r = new EvidenceRing(8, Bpf);

            Assert.False(r.StillHolds(0));
            Assert.False(r.StillHolds(-1));
            Assert.Null(r.Peek(0, out _));
        }

        // ----- exactly full, then wrapped -----

        [Fact]
        public void An_exactly_full_ring_finds_every_frame()
        {
            var r = Filled(capacity: 16, n: 16, from: 500);

            for (long n = 500; n < 516; n++)
                Assert.True(r.StillHolds(n), $"frame {n} should be held");
            Assert.False(r.StillHolds(516));
        }

        /// <summary>
        /// The case the rotation exists for: the oldest frame is no longer at slot 0, and the
        /// ascending order wraps in the middle of the array.
        /// </summary>
        [Fact]
        public void A_wrapped_ring_finds_the_recent_and_forgets_the_old()
        {
            var r = Filled(capacity: 16, n: 20, from: 0);   // 0..19 written, 16 slots

            for (long n = 0; n < 4; n++)
                Assert.False(r.StillHolds(n), $"frame {n} should have been overwritten");
            for (long n = 4; n < 20; n++)
                Assert.True(r.StillHolds(n), $"frame {n} should still be held");
            Assert.False(r.StillHolds(20));
        }

        [Fact]
        public void It_survives_many_wraps()
        {
            var r = Filled(capacity: 7, n: 100, from: 0);

            for (long n = 93; n < 100; n++) Assert.True(r.StillHolds(n), $"{n} held");
            for (long n = 0; n < 93; n++) Assert.False(r.StillHolds(n), $"{n} gone");
        }

        // ----- gaps -----

        /// <summary>
        /// Frame numbers skip when the board misses a frame, which is why this searches instead of
        /// computing frameNumber % Capacity. A ring full of every third number must still be
        /// searchable, and the numbers in between must not be claimed.
        /// </summary>
        [Fact]
        public void Frame_numbers_may_skip()
        {
            var r = Filled(capacity: 16, n: 20, from: 0, step: 3);   // 0,3,6,...,57

            Assert.False(r.StillHolds(0));            // wrapped away
            Assert.True(r.StillHolds(57));            // newest
            Assert.True(r.StillHolds(12));            // 5th of 20, survives a 16-slot ring
            Assert.False(r.StillHolds(13));           // never existed
            Assert.False(r.StillHolds(58));
        }

        // ----- agrees with a scan, over every shape -----

        /// <summary>
        /// The binary search must answer exactly what the scan it replaced would have. Checked
        /// against a reference over every fill level through two wraps, and for every frame number
        /// in range plus the ones just outside.
        /// </summary>
        [Fact]
        public void It_answers_what_a_scan_would_have()
        {
            const int Cap = 13;
            for (int written = 0; written <= Cap * 2 + 3; written++)
            {
                var r = new EvidenceRing(Cap, Bpf);
                var live = new List<long>();
                for (int i = 0; i < written; i++)
                {
                    long n = 100 + i * 2;             // ascending with gaps
                    r.Add(Frame(1), n, 1.0 + i);
                    live.Add(n);
                    if (live.Count > Cap) live.RemoveAt(0);
                }

                for (long n = 96; n <= 100 + written * 2 + 4; n++)
                {
                    bool expected = live.Contains(n);
                    Assert.Equal(expected, r.StillHolds(n));
                }
            }
        }

        // ----- the frame itself, and its time -----

        [Fact]
        public void Peek_returns_the_bytes_that_were_put_in()
        {
            var r = new EvidenceRing(4, Bpf);
            r.Add(Frame(0xAB), frameNumber: 77, boardTimeSec: 12.5);

            byte[] got = r.Peek(77, out double t);

            Assert.NotNull(got);
            Assert.Equal(0xAB, got[0]);
            Assert.Equal(0xAB, got[Bpf - 1]);
            Assert.Equal(12.5, t, 6);
            Assert.Equal(12.5, r.TimeOf(77), 6);
        }

        [Fact]
        public void A_frame_the_ring_no_longer_holds_has_no_time()
        {
            var r = Filled(capacity: 4, n: 10, from: 0);

            Assert.Equal(0.0, r.TimeOf(0));
            Assert.Null(r.Peek(0, out double t));
            Assert.Equal(0.0, t);
        }

        // ----- the window -----

        [Fact]
        public void The_window_is_the_frames_inside_it()
        {
            var r = new EvidenceRing(16, Bpf);
            for (int i = 0; i < 10; i++) r.Add(Frame(1), 200 + i, 50.0 + i);   // t = 50..59

            Assert.True(r.TryWindow(52.0, 55.0, out long first, out long last, out int count));

            Assert.Equal(202, first);
            Assert.Equal(205, last);
            Assert.Equal(4, count);
        }

        [Fact]
        public void A_window_the_ring_missed_is_reported_as_missed()
        {
            var r = new EvidenceRing(16, Bpf);
            for (int i = 0; i < 10; i++) r.Add(Frame(1), 200 + i, 50.0 + i);

            Assert.False(r.TryWindow(90.0, 95.0, out _, out _, out int count));
            Assert.Equal(0, count);
        }

        // ----- refuses what it cannot hold -----

        [Fact]
        public void A_short_frame_is_refused_rather_than_read_past()
        {
            var r = new EvidenceRing(4, Bpf);

            r.Add(new byte[Bpf - 1], 1, 1.0);
            r.Add(null, 2, 2.0);

            Assert.Equal(0, r.Written);
            Assert.False(r.StillHolds(1));
        }

        [Fact]
        public void A_ring_needs_two_slots_and_a_frame_size()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceRing(1, Bpf));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceRing(4, 0));
        }
    }
}
