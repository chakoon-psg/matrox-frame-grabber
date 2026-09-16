using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The start of a grab, which is the one time this detector could fire on a healthy rig.
    ///
    /// A dwell in milliseconds has to be converted to frames, and that needs a frame period,
    /// and a frame period needs two frames. Before then EnterFrames(0) collapses to 1 and the
    /// first flat dark frame becomes a fault - on a grab whose first frames can legitimately be
    /// black, because the panel may not be showing yet and the display buffer still holds the
    /// last run's picture. Found on hardware: the startup line read "dwell 1f in / 1f out".
    /// </summary>
    public class BlackoutDetectorStartupTests
    {
        private const double Period = 1.0 / 120.0;

        private static BlackoutThresholds Half() => new BlackoutThresholds
        {
            EnterLuma = 6.0, ExitLuma = 12.0, MaxSpread = 4.0,
            EnterMs = 500.0, RecoverMs = 500.0,     // 60 frames at 120 fps
        };

        private static TileGrid Flat(long frame, double luma)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 64;
            for (int t = 0; t < TileGrid.TileCount; t++)
                g.Accumulate(t, (long)(luma * n), (long)(luma * luma * n), n);
            return g;
        }

        [Fact]
        public void The_rate_is_not_known_on_the_first_frame()
        {
            var d = new BlackoutDetector(Half());

            Assert.False(d.RateKnown);
            d.Observe(Flat(1, 2.0), 0.0);
            Assert.False(d.RateKnown);              // one timestamp is not a period
            d.Observe(Flat(2, 2.0), Period);
            Assert.True(d.RateKnown);
        }

        /// <summary>
        /// The regression. A black screen from the very first frame must not be confirmed before
        /// the dwell can even be expressed.
        /// </summary>
        [Fact]
        public void A_grab_that_starts_black_is_not_a_fault_on_frame_one()
        {
            var d = new BlackoutDetector(Half());

            Assert.Null(d.Observe(Flat(1, 0.0), 0.0));
            Assert.Null(d.Observe(Flat(2, 0.0), Period));
            Assert.False(d.InBlackout);
        }

        /// <summary>But a grab that starts black and stays black is still reported, once the dwell holds.</summary>
        [Fact]
        public void A_grab_that_starts_black_and_stays_black_is_still_reported()
        {
            var d = new BlackoutDetector(Half());
            AnomalyEvent? got = null;

            for (int i = 0; i < 120; i++)
            {
                AnomalyEvent? e = d.Observe(Flat(i + 1, 0.0), i * Period);
                if (e.HasValue && !got.HasValue) got = e;
            }

            Assert.True(got.HasValue, "60 frames of dwell at 120 fps should confirm");
            Assert.True(got.Value.StartFrame <= 2, "the event starts at the first dark frame");
            Assert.True(d.InBlackout);
        }

        /// <summary>
        /// And the recovery dwell is guarded the same way. A detector reset mid-blackout has no
        /// period until two more frames arrive, and calling the picture back on one frame would
        /// end a fault that is still running.
        /// </summary>
        [Fact]
        public void Recovery_also_waits_for_a_known_rate()
        {
            var d = new BlackoutDetector(Half());
            for (int i = 0; i < 120; i++) d.Observe(Flat(i + 1, 0.0), i * Period);
            Assert.True(d.InBlackout);

            d.Reset();
            Assert.False(d.RateKnown);
            d.Observe(Flat(1, 200.0), 0.0);

            Assert.False(d.InBlackout);   // reset cleared it; the point is nothing threw or fired
            Assert.False(d.RateKnown);
        }
    }
}
