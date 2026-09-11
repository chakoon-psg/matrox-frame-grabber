using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The detector for a picture that has gone and stayed gone.
    ///
    /// It exists because the relative one structurally cannot see this: AnomalyDetector freezes
    /// its baseline during an event so a fault cannot become normal, then has to cap the event at
    /// 2.01 s and adopt the dark level - after which a still-black screen measures a deviation of
    /// zero. Measured 2026-09-10, a sustained occlusion arrives as a run of truncated Dropouts.
    /// </summary>
    public class BlackoutDetectorTests
    {
        private const double Period = 1.0 / 120.0;      // 120 fps, the operating point

        private static BlackoutThresholds Thresholds() => new BlackoutThresholds
        {
            EnterLuma = 6.0,
            ExitLuma = 12.0,
            MaxSpread = 4.0,
            EnterMs = 100.0,          // 12 frames at 120 fps - short, so the tests stay readable
            RecoverMs = 100.0,
        };

        /// <summary>A grid whose tiles all read the same value: flat, which is what a dead panel is.</summary>
        private static TileGrid Flat(long frame, double luma)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 64;
            long sum = (long)(luma * n);
            long sq = (long)(luma * luma * n);
            for (int t = 0; t < TileGrid.TileCount; t++) g.Accumulate(t, sum, sq, n);
            return g;
        }

        /// <summary>
        /// A grid with structure: half the tiles below <paramref name="median"/> and half above,
        /// by <paramref name="spread"/> in total. A dark picture looks like this; a dead panel
        /// does not.
        ///
        /// Straddling the median rather than sitting above it, so that `median` is what
        /// `TileMedian()` actually returns - which is the quantity the detector judges on, the
        /// same one Dropout uses. The first version put both halves above the argument and every
        /// expectation in this file was off by half the spread.
        /// </summary>
        private static TileGrid Structured(long frame, double median, double spread)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 64;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double v = t < TileGrid.TileCount / 2 ? median - spread / 2.0 : median + spread / 2.0;
                if (v < 0.0) v = 0.0;
                g.Accumulate(t, (long)(v * n), (long)(v * v * n), n);
            }
            return g;
        }

        /// <summary>Feeds frames and returns whatever the detector emitted, if anything.</summary>
        private static AnomalyEvent? Feed(BlackoutDetector d, ref long frame, ref double t,
                                          int count, Func<long, TileGrid> make)
        {
            AnomalyEvent? got = null;
            for (int i = 0; i < count; i++)
            {
                AnomalyEvent? e = d.Observe(make(frame), t);
                if (e.HasValue) got = e;
                frame++;
                t += Period;
            }
            return got;
        }

        // ----- what it is for -----

        /// <summary>
        /// A panel that dies and stays dead is reported once, not once every two seconds. That is
        /// the rule this repo settled on for sustained faults, and the reason this class exists.
        /// </summary>
        [Fact]
        public void A_panel_that_stays_black_is_reported_once()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;

            // A healthy picture first, so the frame period is known.
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            AnomalyEvent? first = Feed(d, ref f, ref t, 12, n => Flat(n, 2.0));
            Assert.True(first.HasValue, "the dwell should have confirmed it");
            Assert.Equal(AnomalyKind.Blackout, first.Value.Kind);
            Assert.True(d.InBlackout);

            // Ten more seconds of black: nothing further is emitted.
            AnomalyEvent? more = Feed(d, ref f, ref t, 1200, n => Flat(n, 2.0));
            Assert.False(more.HasValue, "a sustained fault is one report");
            Assert.True(d.InBlackout);
            Assert.True(d.RunningFrames > 1200);
        }

        /// <summary>
        /// The distinguishing test. Same luma, but the tiles disagree - that is a dark picture,
        /// and it must not be a fault. This half of the test needs no calibrated absolute level,
        /// which is why it carries the weight until the optical setup lands.
        /// </summary>
        [Fact]
        public void A_dark_picture_is_not_a_blackout()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            // Median 5 is dark enough to qualify, but 10 luma of structure across the tiles.
            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Structured(n, 5.0, 10.0));

            Assert.False(e.HasValue);
            Assert.False(d.InBlackout);
            Assert.True(d.LastSpread > 4.0);
        }

        [Fact]
        public void A_flat_but_bright_screen_is_not_a_blackout()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;

            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Flat(n, 120.0));

            Assert.False(e.HasValue);
            Assert.False(d.InBlackout);
        }

        // ----- the dwell -----

        [Fact]
        public void One_flat_dark_frame_is_a_cut_to_black_and_not_a_fault()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            // Eleven frames of black, one short of the twelve-frame dwell.
            AnomalyEvent? e = Feed(d, ref f, ref t, 11, n => Flat(n, 2.0));

            Assert.False(e.HasValue);
            Assert.False(d.InBlackout);
        }

        [Fact]
        public void The_event_starts_at_the_first_dark_frame_not_the_confirming_one()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));
            long firstDark = f;

            AnomalyEvent? e = Feed(d, ref f, ref t, 12, n => Flat(n, 2.0));

            Assert.True(e.HasValue);
            Assert.Equal(firstDark, e.Value.StartFrame);
            Assert.Equal(12, e.Value.FrameCount);
        }

        // ----- hysteresis -----

        /// <summary>
        /// The gap between the two levels is the hysteresis. A panel sitting in it is neither
        /// starting a fault nor ending one, and must not chatter.
        /// </summary>
        [Fact]
        public void Sitting_between_the_two_levels_changes_nothing()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));
            Feed(d, ref f, ref t, 12, n => Flat(n, 2.0));
            Assert.True(d.InBlackout);

            // 9 luma: above EnterLuma 6, below ExitLuma 12.
            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Flat(n, 9.0));

            Assert.False(e.HasValue);
            Assert.True(d.InBlackout);          // not called back
            Assert.False(d.RecoveredThisFrame);
        }

        [Fact]
        public void The_picture_coming_back_past_the_exit_level_ends_it()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));
            Feed(d, ref f, ref t, 12, n => Flat(n, 2.0));
            Assert.True(d.InBlackout);

            Feed(d, ref f, ref t, 12, n => Structured(n, 60.0, 20.0));

            Assert.False(d.InBlackout);
        }

        /// <summary>And once it is over, a second blackout is reported again.</summary>
        [Fact]
        public void A_second_blackout_after_recovery_is_reported_again()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            Assert.True(Feed(d, ref f, ref t, 12, n => Flat(n, 2.0)).HasValue);
            Feed(d, ref f, ref t, 12, n => Structured(n, 60.0, 20.0));
            Assert.False(d.InBlackout);

            Assert.True(Feed(d, ref f, ref t, 12, n => Flat(n, 2.0)).HasValue);
        }

        [Fact]
        public void An_exit_level_at_or_below_the_entry_is_lifted_rather_than_obeyed()
        {
            var t = new BlackoutThresholds { EnterLuma = 10.0, ExitLuma = 10.0 };
            t.Normalize();
            Assert.True(t.ExitLuma > t.EnterLuma);

            var t2 = new BlackoutThresholds { EnterLuma = 10.0, ExitLuma = 3.0 };
            t2.Normalize();
            Assert.True(t2.ExitLuma > t2.EnterLuma);
        }

        // ----- holes in the numbering -----

        /// <summary>
        /// The level would still read across a hole - this detector needs no delta - but the
        /// dwell is a claim about how long something held, and frames nobody saw cannot support
        /// it. So the run restarts.
        /// </summary>
        [Fact]
        public void A_gap_in_the_numbering_restarts_the_dwell()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            Feed(d, ref f, ref t, 8, n => Flat(n, 2.0));    // 8 of the 12 needed
            f += 50;                                        // the board missed fifty frames
            t += 50 * Period;
            AnomalyEvent? e = Feed(d, ref f, ref t, 8, n => Flat(n, 2.0));

            Assert.False(e.HasValue);                       // the dwell restarted, 8 again
            Assert.Equal(1, d.FramesSkippedForGaps);
        }

        // ----- stopping mid-fault -----

        /// <summary>
        /// A panel that died four frames before the grab ended would otherwise be lost entirely:
        /// the dwell never completed and nothing was emitted.
        /// </summary>
        [Fact]
        public void A_blackout_still_unconfirmed_at_stop_is_reported_as_truncated()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));
            Feed(d, ref f, ref t, 4, n => Flat(n, 2.0));

            AnomalyEvent? e = d.Flush();

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Blackout, e.Value.Kind);
            Assert.True(e.Value.Truncated);
            Assert.Equal(4, e.Value.FrameCount);
        }

        [Fact]
        public void Nothing_to_flush_when_the_picture_was_fine()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 60, n => Structured(n, 60.0, 20.0));

            Assert.Null(d.Flush());
        }

        [Fact]
        public void An_already_reported_blackout_is_not_flushed_again()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));
            Assert.True(Feed(d, ref f, ref t, 12, n => Flat(n, 2.0)).HasValue);

            Assert.Null(d.Flush());
        }

        // ----- what it reports -----

        [Fact]
        public void The_event_says_how_far_below_the_level_it_went()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            AnomalyEvent? e = Feed(d, ref f, ref t, 12, n => Flat(n, 0.0));

            Assert.True(e.HasValue);
            Assert.Equal(1.0, e.Value.MaxDeviation, 3);     // all the way to zero
            // Coherence 1.0 is the truth here: every tile agrees, which is the finding. 0 would
            // read as "the tiles disagreed", which is the opposite.
            Assert.Equal(1.0, e.Value.MaxCoherence, 3);
        }

        [Fact]
        public void A_duration_is_frames_times_the_period()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));

            AnomalyEvent? e = Feed(d, ref f, ref t, 12, n => Flat(n, 2.0));

            Assert.True(e.HasValue);
            Assert.Equal(12 * Period * 1000.0, e.Value.DurationMs, 1);
        }

        // ----- spread -----

        [Fact]
        public void Spread_is_the_range_of_the_tile_means()
        {
            Assert.Equal(0.0, BlackoutDetector.Spread(Flat(1, 5.0)), 3);
            Assert.Equal(10.0, BlackoutDetector.Spread(Structured(1, 5.0, 10.0)), 3);
            Assert.Equal(0.0, BlackoutDetector.Spread(null));
        }

        [Fact]
        public void An_unpopulated_grid_has_no_spread_to_speak_of()
        {
            Assert.Equal(0.0, BlackoutDetector.Spread(new TileGrid { FrameNumber = 1 }), 3);
        }

        // ----- the proposal -----

        /// <summary>
        /// A threshold picked by hand is one nobody can defend. The proposal reports the floor the
        /// working panel actually reached, so a level set below it cannot fire on a healthy screen.
        /// </summary>
        [Fact]
        public void A_run_proposes_half_the_floor_it_measured()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;

            // A picture that dips to 20 luma with 8 of structure at its darkest.
            Feed(d, ref f, ref t, 200, n => Structured(n, 60.0, 20.0));
            Feed(d, ref f, ref t, 50, n => Structured(n, 20.0, 8.0));

            BlackoutProposal p = d.Propose();

            Assert.Equal(20.0, p.MinLuma, 1);
            Assert.Equal(10.0, p.SuggestedEnterLuma, 1);
            Assert.Equal(8.0, p.MaxFlatSpreadAtDarkest, 1);
            Assert.Equal(4.0, p.SuggestedMaxSpread, 1);
        }

        /// <summary>
        /// 10,000 frames is 83 seconds at 120 fps, and the same bar the depth proposal uses. Below
        /// it the proposal says so rather than offering a number.
        /// </summary>
        [Fact]
        public void A_short_run_refuses_to_propose()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 100, n => Structured(n, 60.0, 20.0));

            BlackoutProposal p = d.Propose();

            Assert.False(p.IsUsable);
            Assert.Contains("NOT ENOUGH", p.ToString());
        }

        [Fact]
        public void The_floor_ignores_frames_that_were_a_fault()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 200, n => Structured(n, 40.0, 20.0));
            Feed(d, ref f, ref t, 600, n => Flat(n, 1.0));      // a real blackout

            BlackoutProposal p = d.Propose();

            Assert.Equal(40.0, p.MinLuma, 1);                  // not 1.0
        }

        // ----- dwell arithmetic -----

        [Fact]
        public void A_dwell_in_milliseconds_becomes_frames_at_the_measured_rate()
        {
            var t = new BlackoutThresholds { EnterMs = 500.0, RecoverMs = 250.0 };

            Assert.Equal(60, t.EnterFrames(120.0));
            Assert.Equal(30, t.RecoverFrames(120.0));
            Assert.Equal(5, t.EnterFrames(10.0));              // exposure 100 ms caps the rate
        }

        [Fact]
        public void A_dwell_is_never_zero_frames()
        {
            var t = new BlackoutThresholds { EnterMs = 0.0, RecoverMs = 0.0 };

            Assert.Equal(1, t.EnterFrames(120.0));
            Assert.Equal(1, t.RecoverFrames(120.0));
            Assert.Equal(1, t.EnterFrames(0.0));
        }

        [Fact]
        public void Thresholds_copy_and_clamp()
        {
            var a = new BlackoutThresholds { EnterLuma = -5.0, ExitLuma = 900.0, MaxSpread = -1.0 };
            var b = new BlackoutThresholds();
            b.CopyFrom(a);

            Assert.Equal(0.0, b.EnterLuma);
            Assert.Equal(255.0, b.ExitLuma);
            Assert.Equal(0.0, b.MaxSpread);
            Assert.True(b.ExitLuma > b.EnterLuma);
        }

        [Fact]
        public void Reset_forgets_the_run()
        {
            var d = new BlackoutDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Structured(n, 60.0, 20.0));
            Feed(d, ref f, ref t, 12, n => Flat(n, 2.0));
            Assert.True(d.InBlackout);

            d.Reset();

            Assert.False(d.InBlackout);
            Assert.Equal(0, d.Judged);
            Assert.Equal(0, d.RunningFrames);
        }
    }
}
