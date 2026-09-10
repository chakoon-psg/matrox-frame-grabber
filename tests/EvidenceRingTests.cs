using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The few seconds of uncompressed frames kept in RAM so a fault can be measured again.
    ///
    /// Two things here can go wrong quietly, and both are what these pin down: a ring shorter than
    /// the window it serves, which loses the head of every dump; and a reader that does not notice
    /// the ring overtook it, which writes a frame that is half of two.
    /// </summary>
    public class EvidenceRingTests
    {
        const double Fps = 124.316;

        static EvidenceRing Ring(int frames = 8, int bytes = 4) => new EvidenceRing(frames, bytes);

        static byte[] Frame(int bytes, byte fill)
        {
            var f = new byte[bytes];
            for (int i = 0; i < bytes; i++) f[i] = fill;
            return f;
        }

        // ----- sizing -----

        /// <summary>
        /// The window closes N seconds after the fault, so its head is 2N seconds old by the time
        /// anything is written. A ring of exactly 2N is therefore about to overwrite the first
        /// frame the dump needs, and there has to be room left over to write in.
        /// </summary>
        [Fact]
        public void The_ring_is_longer_than_the_window_it_serves()
        {
            int frames = EvidenceRing.FramesFor(2.0, Fps);
            double seconds = frames / Fps;

            Assert.True(seconds > 4.0 / EvidenceRing.UsableFraction - 0.01,
                        $"a +-2 s window wanted {seconds:F2} s of ring");
            Assert.True(seconds < 4.0 / EvidenceRing.UsableFraction + 0.05);
        }

        /// <summary>
        /// A fault is not a point in time - it runs until it recovers or hits the event cap - and
        /// the window brackets the whole of it. Measured 2026-09-11 with the cap left out: two
        /// dumps of eight reported "1 LOST to the ring", which is this sum missing a term.
        /// </summary>
        [Fact]
        public void The_event_itself_is_part_of_the_window()
        {
            const double Cap = 2.01;   // the default MaxEventMs

            int without = EvidenceRing.FramesFor(2.0, Fps);
            int with = EvidenceRing.FramesFor(2.0, Fps, Cap);

            Assert.True(with > without, "the cap has to lengthen the ring");
            Assert.InRange((with - without) / Fps, Cap / EvidenceRing.UsableFraction - 0.05,
                                                  Cap / EvidenceRing.UsableFraction + 0.05);
        }

        /// <summary>
        /// What the dump clamps to. The scheduler merges events, so the window it asks for can be
        /// several times the ring - measured, 22 merged occurrences wanted 18.8 s of a 9 s ring.
        /// </summary>
        [Fact]
        public void A_ring_says_how_wide_a_window_it_can_serve()
        {
            int frames = EvidenceRing.FramesFor(2.0, Fps, 2.01);
            var ring = new EvidenceRing(frames, 16);

            double usable = ring.UsableSpanSec(Fps);

            Assert.InRange(usable, 6.01 - 0.05, 6.01 + 0.05);    // 2N + cap, which is what it sized for
            Assert.True(usable < frames / Fps, "it cannot serve the whole ring and still write it");
            Assert.Equal(0.0, ring.UsableSpanSec(0.0));
        }

        /// <summary>The number a settings window has to show before anybody agrees to it.</summary>
        [Fact]
        public void The_cost_is_computable_before_a_byte_is_allocated()
        {
            long perChannel = EvidenceRing.BytesFor(2.0, Fps, 1024L * 772 * 3, 2.01);

            Assert.InRange(perChannel / 1e9, 2.6, 2.8);          // 2.68 GB at decim 2
            Assert.InRange(perChannel * 4 / 1e9, 10.4, 11.2);    // four times that at decim 1
        }

        [Fact]
        public void A_window_of_nothing_needs_no_ring()
        {
            Assert.Equal(0, EvidenceRing.FramesFor(0.0, Fps));
            Assert.Equal(0, EvidenceRing.FramesFor(2.0, 0.0));
            Assert.Equal(0, EvidenceRing.FramesFor(0.0, Fps, 2.01));
            Assert.Equal(0L, EvidenceRing.BytesFor(0.0, Fps, 1000));
        }

        [Fact]
        public void The_geometry_is_validated_rather_than_producing_a_useless_ring()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceRing(1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceRing(8, 0));
        }

        // ----- holding frames -----

        [Fact]
        public void A_frame_put_in_comes_back_with_its_time()
        {
            var r = Ring();
            r.Add(Frame(4, 7), frameNumber: 100, boardTimeSec: 1.5);

            byte[] got = r.Peek(100, out double t);
            Assert.NotNull(got);
            Assert.Equal(7, got[0]);
            Assert.Equal(1.5, t, 6);
            Assert.True(r.StillHolds(100));
            Assert.Equal(1, r.Written);
        }

        /// <summary>
        /// It copies. The array handed in is the shared extraction buffer, which goes back to its
        /// pool and is overwritten within milliseconds - keeping the reference would mean the ring
        /// held whatever came next.
        /// </summary>
        [Fact]
        public void The_frame_is_copied_not_referenced()
        {
            var r = Ring();
            byte[] shared = Frame(4, 1);
            r.Add(shared, 1, 0.0);

            for (int i = 0; i < shared.Length; i++) shared[i] = 99;   // the pool reuses it

            byte[] got = r.Peek(1, out _);
            Assert.Equal(1, got[0]);
        }

        [Fact]
        public void The_oldest_frame_goes_when_it_wraps()
        {
            var r = Ring(frames: 4);
            for (int i = 0; i < 6; i++) r.Add(Frame(4, (byte)i), i, i * 0.1);

            Assert.False(r.StillHolds(0));      // overwritten
            Assert.False(r.StillHolds(1));
            Assert.True(r.StillHolds(2));
            Assert.True(r.StillHolds(5));
            Assert.Equal(6, r.Written);
        }

        /// <summary>
        /// The failure this class must not have: a reader that keeps writing after the ring
        /// overtook it produces a file with a frame that is half of two, and nothing downstream
        /// could tell. StillHolds is how the writer finds out.
        /// </summary>
        [Fact]
        public void A_reader_that_the_ring_overtook_is_told_so()
        {
            var r = Ring(frames: 4);
            for (int i = 0; i < 4; i++) r.Add(Frame(4, (byte)i), i, i * 0.1);

            Assert.NotNull(r.Peek(0, out _));
            Assert.True(r.StillHolds(0));

            r.Add(Frame(4, 9), 4, 0.4);          // the ring moves on while the writer is working

            Assert.False(r.StillHolds(0));
            Assert.Null(r.Peek(0, out _));
        }

        // ----- finding the window -----

        [Fact]
        public void The_window_is_the_frames_inside_it_and_nothing_else()
        {
            var r = Ring(frames: 16);
            for (int i = 0; i < 10; i++) r.Add(Frame(4, (byte)i), 1000 + i, i * 1.0);   // 0..9 s

            Assert.True(r.TryWindow(3.0, 6.0, out long first, out long last, out int count));
            Assert.Equal(1003, first);
            Assert.Equal(1006, last);
            Assert.Equal(4, count);
        }

        /// <summary>
        /// Asked for a span the ring no longer covers, it says so. Writing a short file and
        /// calling it the window would be the quiet version of the same failure.
        /// </summary>
        [Fact]
        public void A_window_the_ring_has_lost_is_refused_rather_than_shortened()
        {
            var r = Ring(frames: 4);
            for (int i = 0; i < 10; i++) r.Add(Frame(4, (byte)i), i, i * 1.0);   // holds 6..9 s

            Assert.False(r.TryWindow(0.0, 3.0, out _, out _, out int count));
            Assert.Equal(0, count);

            Assert.True(r.TryWindow(6.0, 9.0, out long first, out long last, out int held));
            Assert.Equal(6, first);
            Assert.Equal(9, last);
            Assert.Equal(4, held);
        }

        [Fact]
        public void An_empty_ring_offers_no_window()
        {
            Assert.False(Ring().TryWindow(0.0, 10.0, out _, out _, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void A_frame_of_the_wrong_size_is_refused_rather_than_half_copied()
        {
            var r = Ring(frames: 4, bytes: 8);
            r.Add(new byte[4], 1, 0.0);      // too short
            r.Add(null, 2, 0.0);

            Assert.Equal(0, r.Written);
            Assert.False(r.StillHolds(1));
        }
    }
}
