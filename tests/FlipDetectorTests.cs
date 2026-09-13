using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The kind no brightness figure can see.
    ///
    /// The first test here is the demonstration of that, and the reason the detector compares
    /// arrangements instead of levels.
    /// </summary>
    public class FlipDetectorTests
    {
        private const double Period = 1.0 / 120.0;
        private const int Pixels = 4096;

        private static FlipThresholds Thresholds() => new FlipThresholds
        {
            MaxMatchRatio = 0.25,
            MinChange = 1.0,
            MinAsymmetry = 2.0,
            EnterMs = 100.0,          // 12 frames at 120 fps
            RecoverMs = 100.0,
            MinLuma = 12.0,
        };

        /// <summary>
        /// A picture with a bright corner, so it is asymmetric about both axes. The drift makes
        /// consecutive frames differ the way a live picture does.
        /// </summary>
        private static TileGrid Scene(long frame, bool flipped, double drift = 0.0)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                int row = t / TileGrid.Columns, col = t % TileGrid.Columns;
                // A ramp across and down: unequal about both axes, and unequal about the diagonal
                // too, so the half turn is distinguishable from either single mirror.
                double v = 40.0 + col * 6.0 + row * 2.0 + drift;
                int index = flipped ? FlipDetector.Mirror(t, FlipAxis.Horizontal) : t;
                g.Accumulate(index, (long)Math.Round(v * Pixels),
                                    (long)Math.Round(v * v * Pixels), Pixels);
            }
            return g;
        }

        /// <summary>A picture identical to its own mirror about both axes.</summary>
        private static TileGrid Symmetric(long frame, double drift = 0.0)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                int row = t / TileGrid.Columns, col = t % TileGrid.Columns;
                double fromEdgeX = Math.Min(col, TileGrid.Columns - 1 - col);
                double fromEdgeY = Math.Min(row, TileGrid.Rows - 1 - row);
                double v = 60.0 + fromEdgeX * 5.0 + fromEdgeY * 5.0 + drift;
                g.Accumulate(t, (long)Math.Round(v * Pixels),
                                (long)Math.Round(v * v * Pixels), Pixels);
            }
            return g;
        }

        private static TileGrid Dark(long frame)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int t = 0; t < TileGrid.TileCount; t++)
                g.Accumulate(t, 3L * Pixels, 9L * Pixels, Pixels);
            return g;
        }

        /// <summary>
        /// A picture whose content moves several luma between frames, the way a screen playing
        /// something does. The still panels on this rig move about 0.06 luma a frame, which is
        /// below <see cref="FlipThresholds.MinChange"/> - so a still rig produces almost no
        /// judgeable pairs, and a proposal has to be built from a picture that is doing something.
        /// </summary>
        private static TileGrid Busy(long frame, bool flipped) =>
            Scene(frame, flipped, 4.0 * Math.Sin(frame * 0.7));

        private static AnomalyEvent? Feed(FlipDetector d, ref long frame, ref double t,
                                          int count, Func<long, TileGrid> make)
        {
            AnomalyEvent? first = null;
            for (int i = 0; i < count; i++)
            {
                AnomalyEvent? e = d.Observe(make(frame), t);
                if (e.HasValue && !first.HasValue) first = e;
                frame++;
                t += Period;
            }
            return first;
        }

        // ----- Why this detector exists -----

        /// <summary>
        /// `CONTEXT.md`: every brightness-derived scalar is invariant under a flip. Here is the
        /// proof, against the four figures the other detectors actually use.
        /// </summary>
        [Fact]
        public void Every_brightness_figure_is_blind_to_a_flip()
        {
            TileGrid upright = Scene(1, flipped: false);
            TileGrid turned = Scene(2, flipped: true);

            Assert.Equal(upright.TileMedian(), turned.TileMedian(), 6);
            Assert.Equal(BlackoutDetector.Spread(upright), BlackoutDetector.Spread(turned), 6);
            Assert.Equal(WashoutDetector.Detail(upright), WashoutDetector.Detail(turned), 6);
            Assert.Equal(FreezeDetector.Change(upright, upright),
                         FreezeDetector.Change(turned, turned), 6);
        }

        [Fact]
        public void Mirroring_twice_is_the_identity()
        {
            foreach (FlipAxis axis in new[] { FlipAxis.Horizontal, FlipAxis.Vertical, FlipAxis.Both })
                for (int i = 0; i < TileGrid.TileCount; i++)
                    Assert.Equal(i, FlipDetector.Mirror(FlipDetector.Mirror(i, axis), axis));
        }

        [Fact]
        public void The_half_turn_is_both_mirrors()
        {
            for (int i = 0; i < TileGrid.TileCount; i++)
                Assert.Equal(FlipDetector.Mirror(FlipDetector.Mirror(i, FlipAxis.Horizontal),
                                                 FlipAxis.Vertical),
                             FlipDetector.Mirror(i, FlipAxis.Both));
        }

        // ----- What it finds -----

        [Fact]
        public void The_moment_the_picture_turns_over_is_found()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, n => Scene(n, false, n * 0.01));

            AnomalyEvent? e = Feed(d, ref f, ref t, 60, n => Scene(n, true, n * 0.01));

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Flip, e.Value.Kind);
            Assert.True(d.InFlip);
            Assert.Equal(FlipAxis.Horizontal, d.Axis);
        }

        [Fact]
        public void It_is_reported_once_however_long_it_lasts()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, n => Scene(n, false, n * 0.01));

            int reports = 0;
            for (int i = 0; i < 2000; i++)
            {
                if (d.Observe(Scene(f, true, f * 0.01), t).HasValue) reports++;
                f++; t += Period;
            }

            Assert.Equal(1, reports);
        }

        /// <summary>Turning it over again is turning it back, not a second fault.</summary>
        [Fact]
        public void Flipping_back_ends_the_fault()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, n => Scene(n, false, n * 0.01));
            Feed(d, ref f, ref t, 60, n => Scene(n, true, n * 0.01));
            Assert.True(d.InFlip);

            Feed(d, ref f, ref t, 60, n => Scene(n, false, n * 0.01));

            Assert.False(d.InFlip);
            Assert.Equal(FlipAxis.None, d.Axis);
        }

        // ----- What it cannot see, stated as tests so it stays stated -----

        /// <summary>
        /// The reference is the previous frame, so a rig that was already flipped when the run
        /// began has no transition to catch. This is the detector's real limit and the reason it
        /// answers "was the picture turned over during this run" rather than "is it the right way
        /// up".
        /// </summary>
        [Fact]
        public void A_rig_that_started_flipped_reads_as_normal()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 600, n => Scene(n, true, n * 0.01)));
            Assert.False(d.InFlip);
        }

        /// <summary>
        /// A picture symmetric about an axis is identical to its own mirror, so turning it over
        /// changes nothing that can be measured. The detector counts those frames rather than
        /// guessing on them.
        /// </summary>
        [Fact]
        public void A_symmetric_picture_cannot_be_judged_and_says_so()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 300, n => Symmetric(n, n * 0.01)));
            Assert.False(d.InFlip);
            Assert.Equal(0, d.Judged);
            Assert.True(d.TooSymmetric > 200);
        }

        // ----- What it refuses -----

        [Fact]
        public void An_ordinary_moving_picture_is_never_reported()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 1200, n => Busy(n, false)));
            Assert.False(d.InFlip);
        }

        /// <summary>
        /// A picture coming back after darkness must not be compared against the darkness - every
        /// arrangement fits a blank frame equally badly, and the ratio would be meaningless.
        /// </summary>
        [Fact]
        public void The_frame_after_darkness_is_not_compared_against_it()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, n => Scene(n, false, n * 0.01));
            Feed(d, ref f, ref t, 30, Dark);

            Assert.Null(Feed(d, ref f, ref t, 300, n => Scene(n, false, n * 0.01)));
            Assert.False(d.InFlip);
        }

        [Fact]
        public void A_flip_shorter_than_the_dwell_is_not_reported()
        {
            var th = Thresholds();
            th.EnterMs = 1000.0;                      // 120 frames
            var d = new FlipDetector(th);
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, n => Scene(n, false, n * 0.01));

            Assert.Null(Feed(d, ref f, ref t, 40, n => Scene(n, true, n * 0.01)));
        }

        // ----- The proposal -----

        [Fact]
        public void A_short_run_is_not_enough_to_propose_from()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 300, n => Busy(n, false));

            Assert.True(d.Judged > 0);          // the pairs were judgeable, there were just few
            Assert.False(d.Propose().IsUsable);
            Assert.Contains("NOT ENOUGH", d.Propose().ToString());
        }

        /// <summary>
        /// The floor here is a minimum, not a maximum: a longer run finds a *better* accidental
        /// match, so a short run proposes a ratio that would fire on an ordinary frame.
        /// </summary>
        [Fact]
        public void The_proposal_halves_the_best_a_mirror_ever_fit()
        {
            var d = new FlipDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 600, n => Busy(n, false));

            FlipProposal p = d.Propose();
            Assert.True(p.BestRatio > 0.0);
            Assert.Equal(p.BestRatio / 2.0, p.SuggestedMaxRatio, 6);
            Assert.True(p.MedianAsymmetry > 0.0);
        }

        // ----- Settings -----

        [Fact]
        public void Settings_round_trip_through_resolve()
        {
            var s = new DetectionSettings();
            KindSettings k = s.For(AnomalyKind.Flip);
            k.FlipMaxMatchRatio = 0.2;
            k.FlipMinChange = 1.5;
            k.FlipMinAsymmetry = 3.0;
            k.FlipEnterMs = 1500.0;
            k.FlipRecoverMs = 900.0;

            FlipThresholds t = s.ResolveFlip(AnomalyKind.Flip);

            Assert.Equal(0.2, t.MaxMatchRatio, 6);
            Assert.Equal(1.5, t.MinChange, 6);
            Assert.Equal(3.0, t.MinAsymmetry, 6);
            Assert.Equal(1500.0, t.EnterMs, 6);
            Assert.Equal(900.0, t.RecoverMs, 6);
        }
    }
}
