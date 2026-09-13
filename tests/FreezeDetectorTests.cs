using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The one kind whose threshold is a floor rather than a ceiling.
    ///
    /// Brightness stays in range while a panel is frozen, so no deviation threshold can see it.
    /// What makes it decidable is sensor noise: a live camera never sends the same tile grid
    /// twice, so "below the noise" means the same data arrived again rather than that the scene
    /// was still.
    /// </summary>
    public class FreezeDetectorTests
    {
        private const double Period = 1.0 / 120.0;

        private static FreezeThresholds Thresholds() => new FreezeThresholds
        {
            EnterDelta = 0.02,
            ExitDelta = 0.06,
            EnterMs = 100.0,          // 12 frames at 120 fps, so the tests stay readable
            RecoverMs = 100.0,
        };

        /// <summary>A grid whose tile means sit at <paramref name="luma"/> plus a fixed offset.</summary>
        private static TileGrid Grid(long frame, double luma)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 4096;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double v = luma + t * 0.5;            // some structure, constant between frames
                g.Accumulate(t, (long)Math.Round(v * n), (long)Math.Round(v * v * n), n);
            }
            return g;
        }

        /// <summary>
        /// A live grid: the same picture with a per-frame wobble standing in for sensor noise.
        /// <paramref name="noise"/> is the change it produces frame to frame.
        /// </summary>
        private static TileGrid Live(long frame, double luma, double noise)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 4096;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                // Alternating sign per frame, so consecutive frames differ by exactly `noise`.
                double wobble = (frame % 2 == 0 ? 1.0 : -1.0) * noise / 2.0;
                double v = luma + t * 0.5 + wobble;
                g.Accumulate(t, (long)Math.Round(v * n), (long)Math.Round(v * v * n), n);
            }
            return g;
        }

        private static AnomalyEvent? Feed(FreezeDetector d, ref long frame, ref double t,
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

        // ----- what it is for -----

        /// <summary>
        /// Identical grids arriving one after another. A live camera cannot produce this, so it is
        /// the same picture again.
        /// </summary>
        [Fact]
        public void The_same_grid_arriving_again_is_a_freeze()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));   // live first, for the period

            // Thirteen and not twelve: the frame that transitions from live to frozen still
            // carries a change, so it is not one of the dwell's frames. A freeze therefore takes
            // dwell + 1 frames from the last live one, which is correct and worth pinning.
            AnomalyEvent? e = Feed(d, ref f, ref t, 13, n => Grid(n, 100.0));

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Freeze, e.Value.Kind);
            Assert.True(d.InFreeze);
            Assert.Equal(0.0, d.LastChange, 6);
        }

        /// <summary>
        /// The case that makes the kind hard. The scene is perfectly still - a settings screen
        /// nobody is touching - but the camera is alive, so its noise keeps the change above the
        /// floor. This must not be a fault.
        /// </summary>
        [Fact]
        public void A_still_scene_on_a_live_camera_is_not_a_freeze()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;

            // 0.1 luma of change per frame: nothing is moving, but the sensor is alive.
            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Live(n, 100.0, 0.1));

            Assert.False(e.HasValue);
            Assert.False(d.InFreeze);
            Assert.True(d.LastChange > 0.02, "the noise is what keeps it above the floor");
        }

        [Fact]
        public void A_moving_picture_is_not_a_freeze()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;

            AnomalyEvent? e = Feed(d, ref f, ref t, 600, n => Live(n, 100.0, 3.0));

            Assert.False(e.HasValue);
            Assert.False(d.InFreeze);
        }

        /// <summary>Brightness is in range throughout - which is why no deviation threshold sees it.</summary>
        [Fact]
        public void A_freeze_happens_at_a_perfectly_normal_brightness()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));

            AnomalyEvent? e = Feed(d, ref f, ref t, 13, n => Grid(n, 100.0));

            Assert.True(e.HasValue);
            // The grid sat at a mid-range luma the whole time; nothing about the level moved.
            Assert.Equal(100.0 + 31.5 * 0.5, Grid(1, 100.0).TileMedian(), 0);
        }

        // ----- sustained behaviour -----

        [Fact]
        public void A_panel_that_stays_frozen_is_reported_once()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));

            Assert.True(Feed(d, ref f, ref t, 13, n => Grid(n, 100.0)).HasValue);
            AnomalyEvent? more = Feed(d, ref f, ref t, 1200, n => Grid(n, 100.0));

            Assert.False(more.HasValue);
            Assert.True(d.RunningFrames > 1200);
        }

        [Fact]
        public void A_couple_of_identical_frames_is_not_a_frozen_panel()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));

            AnomalyEvent? e = Feed(d, ref f, ref t, 11, n => Grid(n, 100.0));

            Assert.False(e.HasValue);
        }

        [Fact]
        public void The_picture_moving_again_ends_it()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));
            Feed(d, ref f, ref t, 13, n => Grid(n, 100.0));
            Assert.True(d.InFreeze);

            Feed(d, ref f, ref t, 12, n => Live(n, 100.0, 0.5));

            Assert.False(d.InFreeze);
        }

        /// <summary>Change between the two levels is neither starting a fault nor ending one.</summary>
        [Fact]
        public void Sitting_between_the_two_levels_changes_nothing()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));
            Feed(d, ref f, ref t, 13, n => Grid(n, 100.0));
            Assert.True(d.InFreeze);

            // 0.04: above EnterDelta 0.02, below ExitDelta 0.06.
            Feed(d, ref f, ref t, 600, n => Live(n, 100.0, 0.04));

            Assert.True(d.InFreeze, "not called back while it sits in the gap");
        }

        [Fact]
        public void An_exit_at_or_below_the_entry_is_lifted()
        {
            var a = new FreezeThresholds { EnterDelta = 0.02, ExitDelta = 0.02 };
            a.Normalize();
            Assert.True(a.ExitDelta > a.EnterDelta);

            var b = new FreezeThresholds { EnterDelta = 0.02, ExitDelta = 0.001 };
            b.Normalize();
            Assert.True(b.ExitDelta > b.EnterDelta);
        }

        [Fact]
        public void The_first_frame_has_nothing_to_compare_against()
        {
            var d = new FreezeDetector(Thresholds());

            Assert.Null(d.Observe(Grid(1, 100.0), 0.0));
            Assert.Equal(0, d.Judged);          // not judged, only remembered
        }

        [Fact]
        public void A_gap_in_the_numbering_restarts_the_dwell()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));

            Feed(d, ref f, ref t, 8, n => Grid(n, 100.0));
            f += 50; t += 50 * Period;
            AnomalyEvent? e = Feed(d, ref f, ref t, 8, n => Grid(n, 100.0));

            Assert.False(e.HasValue);
            Assert.Equal(1, d.FramesSkippedForGaps);
        }

        [Fact]
        public void A_freeze_unconfirmed_at_stop_is_reported_as_truncated()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));
            Feed(d, ref f, ref t, 4, n => Grid(n, 100.0));

            AnomalyEvent? e = d.Flush();

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Freeze, e.Value.Kind);
            Assert.True(e.Value.Truncated);
        }

        // ----- the measure -----

        [Fact]
        public void Change_is_the_mean_absolute_difference_of_tile_means()
        {
            Assert.Equal(0.0, FreezeDetector.Change(Grid(1, 100.0), Grid(2, 100.0)), 6);
            Assert.Equal(5.0, FreezeDetector.Change(Grid(1, 100.0), Grid(2, 105.0)), 3);
            Assert.Equal(0.0, FreezeDetector.Change(null, Grid(1, 100.0)));
        }

        /// <summary>
        /// Mean and not max, so one tile catching a reflection cannot hide a screen that has
        /// otherwise stopped.
        /// </summary>
        [Fact]
        public void One_moving_tile_does_not_hide_a_stopped_screen()
        {
            var a = Grid(1, 100.0);
            var b = new TileGrid { FrameNumber = 2 };
            const int n = 4096;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double v = 100.0 + t * 0.5 + (t == 0 ? 40.0 : 0.0);   // one tile jumps
                b.Accumulate(t, (long)Math.Round(v * n), (long)Math.Round(v * v * n), n);
            }

            // 40 / 64 is well under a luma: the screen stopped whatever that one tile did.
            Assert.True(FreezeDetector.Change(a, b) < 1.0);
        }

        // ----- the proposal -----

        /// <summary>
        /// The number the whole kind rests on. A threshold above the noise floor fires on a static
        /// screen; far below it, nothing ever fires.
        /// </summary>
        [Fact]
        public void A_run_proposes_half_the_quietest_live_change_it_saw()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 300, n => Live(n, 100.0, 0.4));

            FreezeProposal p = d.Propose();

            Assert.Equal(0.4, p.QuietestChange, 2);
            Assert.Equal(0.2, p.SuggestedEnterDelta, 2);
            Assert.True(p.SawLiveFrames);
        }

        /// <summary>
        /// A frozen stretch must not define the floor as its own near-zero change, or the
        /// threshold it proposes would be unreachable.
        /// </summary>
        [Fact]
        public void A_frozen_stretch_does_not_set_the_noise_floor()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 300, n => Live(n, 100.0, 0.4));
            Feed(d, ref f, ref t, 600, n => Grid(n, 100.0));      // a real freeze

            FreezeProposal p = d.Propose();

            // The claim is that the frozen run does not drag the floor to zero. The frame that
            // transitioned into it is a live frame and does count, which is why this is a bound
            // rather than an equality.
            Assert.True(p.QuietestChange > 0.1, $"floor collapsed to {p.QuietestChange}");
        }

        [Fact]
        public void A_short_run_refuses_to_propose()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 100, n => Live(n, 100.0, 0.4));

            Assert.False(d.Propose().IsUsable);
            Assert.Contains("NOT ENOUGH", d.Propose().ToString());
        }

        [Fact]
        public void Reset_forgets_the_run()
        {
            var d = new FreezeDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 30, n => Live(n, 100.0, 0.5));
            Feed(d, ref f, ref t, 13, n => Grid(n, 100.0));
            Assert.True(d.InFreeze);

            d.Reset();

            Assert.False(d.InFreeze);
            Assert.Equal(0, d.Judged);
        }
    }
}
