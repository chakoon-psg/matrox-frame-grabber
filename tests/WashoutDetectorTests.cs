using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The detector for a picture that went bright and lost its structure.
    ///
    /// CONTEXT.md is explicit that this is not a brightness test: "밝은데 구조가 없는" - a white UI
    /// background and a saturated panel have the same mean brightness, and only the structure
    /// tells them apart. Most of these tests are about that distinction.
    /// </summary>
    public class WashoutDetectorTests
    {
        private const double Period = 1.0 / 120.0;

        private static WashoutThresholds Thresholds() => new WashoutThresholds
        {
            EnterLuma = 200.0,
            ExitLuma = 180.0,
            MaxSpread = 4.0,
            MaxDetail = 3.0,
            EnterMs = 100.0,          // 12 frames at 120 fps
            RecoverMs = 100.0,
        };

        /// <summary>
        /// A grid with a chosen tile median, between-tile spread, and within-tile detail.
        ///
        /// Detail is built by giving each tile a variance: sqSum is set so that
        /// stdev = sqrt(sqSum/n - mean^2) comes out at the value asked for.
        /// </summary>
        private static TileGrid Grid(long frame, double median, double spread, double detail)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 64;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double mean = t < TileGrid.TileCount / 2 ? median - spread / 2.0 : median + spread / 2.0;
                if (mean < 0.0) mean = 0.0;
                double sq = (mean * mean + detail * detail) * n;
                g.Accumulate(t, (long)Math.Round(mean * n), (long)Math.Round(sq), n);
            }
            return g;
        }

        private static AnomalyEvent? Feed(WashoutDetector d, ref long frame, ref double t,
                                          int count, Func<long, TileGrid> make)
        {
            AnomalyEvent? got = null;
            for (int i = 0; i < count; i++)
            {
                AnomalyEvent? e = d.Observe(make(frame), t);
                if (e.HasValue && !got.HasValue) got = e;
                frame++;
                t += Period;
            }
            return got;
        }

        /// <summary>Enough healthy frames to establish the frame period.</summary>
        private static void Settle(WashoutDetector d, ref long f, ref double t) =>
            Feed(d, ref f, ref t, 30, n => Grid(n, 90.0, 40.0, 20.0));

        // ----- the distinction that matters -----

        /// <summary>
        /// The test this detector exists for. Same brightness, but the tiles still carry text -
        /// that is a white screen, not a fault. Without the detail measure this would fire.
        /// </summary>
        [Fact]
        public void A_white_screen_with_text_on_it_is_not_a_washout()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            // Bright and flat between tiles, but 12 luma of variance inside them.
            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Grid(n, 230.0, 1.0, 12.0));

            Assert.False(e.HasValue);
            Assert.False(d.InWashout);
            Assert.True(d.LastDetail > 3.0, "the detail is what saved it");
        }

        [Fact]
        public void A_saturated_panel_with_no_structure_at_all_is_a_washout()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            AnomalyEvent? e = Feed(d, ref f, ref t, 12, n => Grid(n, 250.0, 0.5, 0.5));

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Washout, e.Value.Kind);
            Assert.True(d.InWashout);
        }

        /// <summary>Structureless but not bright is Blackout's business, not this one.</summary>
        [Fact]
        public void A_dark_flat_frame_is_not_a_washout()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Grid(n, 3.0, 0.2, 0.2));

            Assert.False(e.HasValue);
            Assert.False(d.InWashout);
        }

        /// <summary>Bright with structure between tiles - a picture, however bright.</summary>
        [Fact]
        public void A_bright_picture_with_large_scale_structure_is_not_a_washout()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Grid(n, 220.0, 30.0, 1.0));

            Assert.False(e.HasValue);
            Assert.True(d.LastSpread > 4.0);
        }

        // ----- the sustained-kind behaviour, shared with Blackout -----

        [Fact]
        public void A_panel_that_stays_washed_out_is_reported_once()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            Assert.True(Feed(d, ref f, ref t, 12, n => Grid(n, 250.0, 0.5, 0.5)).HasValue);
            AnomalyEvent? more = Feed(d, ref f, ref t, 1200, n => Grid(n, 250.0, 0.5, 0.5));

            Assert.False(more.HasValue, "a sustained fault is one report");
            Assert.True(d.RunningFrames > 1200);
        }

        [Fact]
        public void One_bright_flat_frame_is_a_cut_to_white_and_not_a_fault()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            AnomalyEvent? e = Feed(d, ref f, ref t, 11, n => Grid(n, 250.0, 0.5, 0.5));

            Assert.False(e.HasValue);
        }

        /// <summary>The hysteresis runs the other way here: exit is below entry.</summary>
        [Fact]
        public void Sitting_between_the_two_levels_changes_nothing()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);
            Feed(d, ref f, ref t, 12, n => Grid(n, 250.0, 0.5, 0.5));
            Assert.True(d.InWashout);

            // 190: below EnterLuma 200, above ExitLuma 180.
            Feed(d, ref f, ref t, 600, n => Grid(n, 190.0, 0.5, 0.5));

            Assert.True(d.InWashout, "not called back while it sits in the gap");
        }

        [Fact]
        public void Falling_back_past_the_exit_level_ends_it()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);
            Feed(d, ref f, ref t, 12, n => Grid(n, 250.0, 0.5, 0.5));
            Assert.True(d.InWashout);

            Feed(d, ref f, ref t, 12, n => Grid(n, 90.0, 40.0, 20.0));

            Assert.False(d.InWashout);
        }

        [Fact]
        public void An_exit_at_or_above_the_entry_is_pushed_below_it()
        {
            var a = new WashoutThresholds { EnterLuma = 200.0, ExitLuma = 200.0 };
            a.Normalize();
            Assert.True(a.ExitLuma < a.EnterLuma);

            var b = new WashoutThresholds { EnterLuma = 200.0, ExitLuma = 240.0 };
            b.Normalize();
            Assert.True(b.ExitLuma < b.EnterLuma);
        }

        [Fact]
        public void Nothing_confirms_before_the_rate_is_known()
        {
            var d = new WashoutDetector(new WashoutThresholds
            {
                EnterLuma = 200.0, ExitLuma = 180.0, MaxSpread = 4.0, MaxDetail = 3.0,
                EnterMs = 500.0, RecoverMs = 500.0,
            });

            Assert.Null(d.Observe(Grid(1, 250.0, 0.5, 0.5), 0.0));
            Assert.Null(d.Observe(Grid(2, 250.0, 0.5, 0.5), Period));
            Assert.False(d.InWashout);
            Assert.True(d.RateKnown);
        }

        [Fact]
        public void A_gap_in_the_numbering_restarts_the_dwell()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);

            Feed(d, ref f, ref t, 8, n => Grid(n, 250.0, 0.5, 0.5));
            f += 50; t += 50 * Period;
            AnomalyEvent? e = Feed(d, ref f, ref t, 8, n => Grid(n, 250.0, 0.5, 0.5));

            Assert.False(e.HasValue);
            Assert.Equal(1, d.FramesSkippedForGaps);
        }

        [Fact]
        public void A_washout_unconfirmed_at_stop_is_reported_as_truncated()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);
            Feed(d, ref f, ref t, 4, n => Grid(n, 250.0, 0.5, 0.5));

            AnomalyEvent? e = d.Flush();

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Washout, e.Value.Kind);
            Assert.True(e.Value.Truncated);
        }

        // ----- the detail measure -----

        [Fact]
        public void Detail_is_the_mean_of_the_per_tile_deviations()
        {
            Assert.Equal(0.0, WashoutDetector.Detail(Grid(1, 100.0, 0.0, 0.0)), 1);
            Assert.Equal(7.0, WashoutDetector.Detail(Grid(1, 100.0, 0.0, 7.0)), 0);
            Assert.Equal(0.0, WashoutDetector.Detail(null));
        }

        /// <summary>
        /// The mean rather than the max, so one tile keeping an edge does not excuse a screen
        /// that has otherwise gone blank - and so a single bright reflection cannot dominate.
        /// </summary>
        [Fact]
        public void One_tile_keeping_an_edge_does_not_excuse_the_rest()
        {
            var g = new TileGrid { FrameNumber = 1 };
            const int n = 64;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double detail = t == 0 ? 60.0 : 0.0;       // one tile of edge, 63 blank
                double mean = 250.0;
                g.Accumulate(t, (long)(mean * n), (long)((mean * mean + detail * detail) * n), n);
            }

            // 60 / 64 is under one luma: the screen is blank whatever that one tile is doing.
            Assert.True(WashoutDetector.Detail(g) < 1.0);
        }

        // ----- the proposal -----

        [Fact]
        public void A_run_proposes_halfway_from_the_panel_ceiling_to_saturation()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 300, n => Grid(n, 120.0, 40.0, 20.0));

            WashoutProposal p = d.Propose();

            Assert.Equal(120.0, p.MaxLuma, 0);
            Assert.Equal(120.0 + (255.0 - 120.0) / 2.0, p.SuggestedEnterLuma, 0);   // 187.5
            Assert.Equal(20.0, p.DetailAtBrightest, 0);
            Assert.Equal(5.0, p.SuggestedMaxDetail, 0);
        }

        [Fact]
        public void A_proposal_never_suggests_a_level_nothing_can_reach()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            // A panel already at 254: halfway to 255 would round to 255, which nothing reaches.
            Feed(d, ref f, ref t, 300, n => Grid(n, 254.0, 30.0, 20.0));

            WashoutProposal p = d.Propose();

            Assert.True(p.SuggestedEnterLuma <= 254.0);
        }

        [Fact]
        public void A_short_run_refuses_to_propose()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 100, n => Grid(n, 120.0, 40.0, 20.0));

            Assert.False(d.Propose().IsUsable);
            Assert.Contains("NOT ENOUGH", d.Propose().ToString());
        }

        [Fact]
        public void A_run_that_was_washed_out_throughout_has_no_ceiling_to_report()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 300, n => Grid(n, 250.0, 0.5, 0.5));

            WashoutProposal p = d.Propose();

            Assert.False(p.SawHealthyFrame);
            Assert.Contains("no frame was outside a washout", p.ToString());
        }

        [Fact]
        public void Reset_forgets_the_run()
        {
            var d = new WashoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Settle(d, ref f, ref t);
            Feed(d, ref f, ref t, 12, n => Grid(n, 250.0, 0.5, 0.5));
            Assert.True(d.InWashout);

            d.Reset();

            Assert.False(d.InWashout);
            Assert.Equal(0, d.Judged);
        }
    }
}
