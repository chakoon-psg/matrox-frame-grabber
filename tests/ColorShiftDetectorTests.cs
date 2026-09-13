using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The one kind luma cannot see.
    ///
    /// `CONTEXT.md` says an R-B exchange cancels exactly once the bands are weighted into one
    /// number, so the first test here is not about the detector at all - it is the demonstration
    /// that every other detector in this repo is blind to this fault, which is why a fifth one
    /// exists.
    /// </summary>
    public class ColorShiftDetectorTests
    {
        private const double Period = 1.0 / 120.0;
        private const int Pixels = 4096;

        /// <summary>Healthy shares, near enough to what a panel under this lighting produces.</summary>
        private const double BaseR = 0.30, BaseG = 0.42, BaseB = 0.28;

        private static ColorShiftThresholds Thresholds() => new ColorShiftThresholds
        {
            BaselineR = BaseR,
            BaselineG = BaseG,
            BaselineB = BaseB,
            MaxDeviation = 0.05,
            ExitDeviation = 0.03,
            MinLuma = 12.0,
            EnterMs = 100.0,          // 12 frames at 120 fps, so the tests stay readable
            RecoverMs = 100.0,
        };

        /// <summary>
        /// A grid at a given brightness whose three bands carry the given shares.
        ///
        /// The tile means are filled in as well, because the detector gates on luma before it
        /// looks at a share - a grid with bands and no tiles would be judged as too dark.
        /// </summary>
        private static TileGrid Grid(long frame, double luma, double r, double g, double b)
        {
            var grid = new TileGrid { FrameNumber = frame };
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double v = luma + t * 0.25;
                grid.Accumulate(t, (long)Math.Round(v * Pixels),
                                   (long)Math.Round(v * v * Pixels), Pixels);
            }

            // Three band totals whose shares are exactly (r, g, b) of a mean level of `luma`.
            double total = luma * 3.0;
            grid.AccumulateBands((long)Math.Round(r * total * Pixels),
                                 (long)Math.Round(g * total * Pixels),
                                 (long)Math.Round(b * total * Pixels),
                                 Pixels);
            return grid;
        }

        private static TileGrid Healthy(long frame) => Grid(frame, 100.0, BaseR, BaseG, BaseB);

        private static AnomalyEvent? Feed(ColorShiftDetector d, ref long frame, ref double t,
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

        // ----- The reason this detector exists -----

        /// <summary>
        /// Swap two bands and every luma-derived figure in this repo stays where it was.
        ///
        /// Not quite to the last digit - the luma weights are 0.299 and 0.114, so exchanging R and
        /// B does move the mean. What matters is that it moves it by less than the sensor noise
        /// the other detectors are already told to ignore, which is the same thing as being
        /// invisible to them.
        /// </summary>
        [Fact]
        public void A_band_swap_barely_moves_any_luma_figure()
        {
            TileGrid before = Grid(1, 100.0, BaseR, BaseG, BaseB);
            TileGrid after = Grid(2, 100.0, BaseB, BaseG, BaseR);

            Assert.Equal(before.TileMedian(), after.TileMedian(), 6);
            Assert.Equal(BlackoutDetector.Spread(before), BlackoutDetector.Spread(after), 6);
            Assert.Equal(WashoutDetector.Detail(before), WashoutDetector.Detail(after), 6);
        }

        /// <summary>And the shares move by the full gap between the two bands.</summary>
        [Fact]
        public void A_band_swap_moves_the_shares_by_the_gap_between_them()
        {
            var d = new ColorShiftDetector(Thresholds());
            d.Observe(Grid(1, 100.0, BaseB, BaseG, BaseR), 0.0);

            Assert.Equal(Math.Abs(BaseR - BaseB), d.LastDeviation, 3);
        }

        // ----- Confirming -----

        [Fact]
        public void Healthy_colour_is_never_reported()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 600, Healthy));
            Assert.False(d.InColorShift);
        }

        [Fact]
        public void A_missing_band_is_reported_once_the_dwell_is_served()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, Healthy);

            // Red gone, the rest sharing what is left.
            AnomalyEvent? e = Feed(d, ref f, ref t, 60,
                                   n => Grid(n, 100.0, 0.0, 0.60, 0.40));

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.ColorShift, e.Value.Kind);
            Assert.True(d.InColorShift);
        }

        [Fact]
        public void A_shift_shorter_than_the_dwell_is_not_reported()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, Healthy);

            Assert.Null(Feed(d, ref f, ref t, 6, n => Grid(n, 100.0, 0.0, 0.60, 0.40)));
            Assert.Null(Feed(d, ref f, ref t, 60, Healthy));
        }

        [Fact]
        public void A_shift_is_reported_once_however_long_it_lasts()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, Healthy);

            int reports = 0;
            for (int i = 0; i < 2000; i++)
            {
                if (d.Observe(Grid(f, 100.0, 0.0, 0.60, 0.40), t).HasValue) reports++;
                f++; t += Period;
            }

            Assert.Equal(1, reports);
        }

        [Fact]
        public void The_picture_getting_brighter_is_not_a_colour_shift()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, Healthy);

            // Twice the light, the same colour. A share does not move with brightness, and that
            // is the property that keeps this kind from reporting the other four.
            Assert.Null(Feed(d, ref f, ref t, 600, n => Grid(n, 200.0, BaseR, BaseG, BaseB)));
            Assert.False(d.InColorShift);
        }

        // ----- What it refuses to judge -----

        /// <summary>
        /// A dark frame's shares are noise divided by noise. Reporting there would mean every
        /// blackout arrived twice, once under the wrong name.
        /// </summary>
        [Fact]
        public void A_dark_frame_is_left_to_blackout()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, Healthy);

            Assert.Null(Feed(d, ref f, ref t, 600, n => Grid(n, 3.0, 0.9, 0.05, 0.05)));
            Assert.False(d.InColorShift);
        }

        [Fact]
        public void A_dark_frame_is_not_counted_as_judged()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 100, n => Grid(n, 3.0, BaseR, BaseG, BaseB));

            Assert.Equal(0, d.Judged);
        }

        /// <summary>A mono ring carries no bands, and that is not a fault.</summary>
        [Fact]
        public void A_grid_without_bands_is_not_judged()
        {
            var d = new ColorShiftDetector(Thresholds());
            var g = new TileGrid { FrameNumber = 1 };
            for (int i = 0; i < TileGrid.TileCount; i++)
                g.Accumulate(i, 100 * Pixels, 10000L * Pixels, Pixels);

            Assert.False(g.HasBands);
            Assert.Null(d.Observe(g, 0.0));
            Assert.Equal(0, d.Judged);
        }

        // ----- Uncalibrated -----

        /// <summary>
        /// With no baseline there is no reference, and a detector that invented one from its own
        /// first frames would let the start of a run define what correct means for the rest of it.
        /// </summary>
        [Fact]
        public void Without_a_baseline_nothing_is_reported()
        {
            var d = new ColorShiftDetector(new ColorShiftThresholds { MinLuma = 12.0 });
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 600, n => Grid(n, 100.0, 0.0, 0.60, 0.40)));
            Assert.False(d.Calibrated);
            Assert.False(d.InColorShift);
        }

        [Fact]
        public void Without_a_baseline_it_still_measures_one()
        {
            var d = new ColorShiftDetector(new ColorShiftThresholds { MinLuma = 12.0 });
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 500, Healthy);

            ColorShiftProposal p = d.Propose();
            Assert.Equal(BaseR, p.BaselineR, 3);
            Assert.Equal(BaseG, p.BaselineG, 3);
            Assert.Equal(BaseB, p.BaselineB, 3);
            Assert.Equal(500, p.FramesJudged);
        }

        // ----- The proposal -----

        [Fact]
        public void The_floor_is_the_furthest_a_share_moved_from_its_own_mean()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 100, Healthy);
            // One frame two points of share off, on the low side.
            Feed(d, ref f, ref t, 1, n => Grid(n, 100.0, BaseR - 0.02, BaseG + 0.02, BaseB));

            ColorShiftProposal p = d.Propose();
            Assert.InRange(p.Floor, 0.015, 0.025);
            Assert.Equal(p.Floor * 2.0, p.SuggestedMaxDeviation, 6);
        }

        [Fact]
        public void A_short_run_is_not_enough_to_propose_from()
        {
            var d = new ColorShiftDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 100, Healthy);

            Assert.False(d.Propose().IsUsable);
            Assert.Contains("NOT ENOUGH", d.Propose().ToString());
        }

        // ----- Settings -----

        [Fact]
        public void The_exit_is_pulled_under_the_entry()
        {
            var t = new ColorShiftThresholds { MaxDeviation = 0.10, ExitDeviation = 0.20 };
            t.Normalize();

            Assert.True(t.ExitDeviation < t.MaxDeviation);
        }

        [Fact]
        public void A_zero_baseline_means_uncalibrated_not_a_baseline_of_zero()
        {
            Assert.False(new ColorShiftThresholds().Calibrated);
            Assert.False(new ColorShiftThresholds { BaselineR = 0.3, BaselineG = 0.4 }.Calibrated);
            Assert.True(new ColorShiftThresholds
            {
                BaselineR = 0.3, BaselineG = 0.4, BaselineB = 0.3,
            }.Calibrated);
        }

        [Fact]
        public void Settings_round_trip_through_resolve()
        {
            var s = new DetectionSettings();
            KindSettings k = s.For(AnomalyKind.ColorShift);
            k.ColorShiftBaselineR = 0.31;
            k.ColorShiftBaselineG = 0.41;
            k.ColorShiftBaselineB = 0.28;
            k.ColorShiftMaxDeviation = 0.08;
            k.ColorShiftExitDeviation = 0.05;
            k.ColorShiftMinLuma = 15.0;
            k.ColorShiftEnterMs = 1500.0;
            k.ColorShiftRecoverMs = 700.0;

            ColorShiftThresholds t = s.ResolveColorShift(AnomalyKind.ColorShift);

            Assert.Equal(0.31, t.BaselineR, 6);
            Assert.Equal(0.41, t.BaselineG, 6);
            Assert.Equal(0.28, t.BaselineB, 6);
            Assert.Equal(0.08, t.MaxDeviation, 6);
            Assert.Equal(0.05, t.ExitDeviation, 6);
            Assert.Equal(15.0, t.MinLuma, 6);
            Assert.Equal(1500.0, t.EnterMs, 6);
            Assert.Equal(700.0, t.RecoverMs, 6);
        }

        // ----- The grid itself -----

        [Fact]
        public void Band_totals_survive_a_copy_and_are_cleared_by_a_reset()
        {
            TileGrid a = Healthy(7);
            var b = new TileGrid();
            b.CopyFrom(a);

            Assert.True(b.HasBands);
            Assert.Equal(a.BandMeanR, b.BandMeanR, 6);
            Assert.Equal(a.BandMeanG, b.BandMeanG, 6);
            Assert.Equal(a.BandMeanB, b.BandMeanB, 6);

            b.Reset();
            Assert.False(b.HasBands);
            Assert.Equal(1.0, b.BandBalance, 6);
        }

        /// <summary>
        /// Balance is what a dead band drives to zero. It is deliberately blind to a swap - min
        /// over max cannot tell which band is which - which is why the detector judges shares
        /// against a baseline instead, and balance is only ever reported.
        /// </summary>
        [Fact]
        public void Balance_finds_a_dead_band_and_cannot_find_a_swap()
        {
            TileGrid healthy = Grid(1, 100.0, BaseR, BaseG, BaseB);
            TileGrid swapped = Grid(2, 100.0, BaseB, BaseG, BaseR);
            TileGrid dead = Grid(3, 100.0, 0.0, 0.60, 0.40);

            Assert.Equal(healthy.BandBalance, swapped.BandBalance, 6);
            Assert.Equal(0.0, dead.BandBalance, 6);
        }
    }
}
