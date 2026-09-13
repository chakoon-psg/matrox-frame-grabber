using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The kind whose difficulty is entirely in what it must refuse.
    ///
    /// Finding an oscillation in a brightness series is easy. Not reporting a dropout, a panel
    /// dimming, or the alias of a backlight that runs a thousand times faster than the frame rate
    /// is the work, so most of these tests are about things that must stay quiet.
    /// </summary>
    public class FlickerDetectorTests
    {
        private const double Fps = 120.0;
        private const double Period = 1.0 / Fps;
        private const int Pixels = 4096;
        private const int Window = 60;              // half a second, so the tests stay readable

        private static FlickerThresholds Thresholds() => new FlickerThresholds
        {
            EnterAmplitude = 0.05,
            ExitAmplitude = 0.03,
            MinHz = 3.0,
            MaxHz = 25.0,
            WindowFrames = Window,
            MinCrossings = 6,
            EnterMs = 100.0,
            RecoverMs = 100.0,
            MinLuma = 12.0,
        };

        /// <summary>A grid whose tiles all sit at <paramref name="luma"/> plus a little structure.</summary>
        private static TileGrid Grid(long frame, double luma)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double v = luma + t * 0.25;
                g.Accumulate(t, (long)Math.Round(v * Pixels),
                                (long)Math.Round(v * v * Pixels), Pixels);
            }
            return g;
        }

        /// <summary>Brightness swinging by <paramref name="depth"/> of its mean at a given rate.</summary>
        private static TileGrid Oscillating(long frame, double luma, double depth, double hz)
        {
            double phase = 2.0 * Math.PI * hz * (frame / Fps);
            return Grid(frame, luma * (1.0 + depth / 2.0 * Math.Sin(phase)));
        }

        private static AnomalyEvent? Feed(FlickerDetector d, ref long frame, ref double t,
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

        // ----- What it finds -----

        [Fact]
        public void A_ten_hertz_oscillation_is_reported()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;

            AnomalyEvent? e = Feed(d, ref f, ref t, 400,
                                   n => Oscillating(n, 100.0, 0.20, 10.0));

            Assert.True(e.HasValue);
            Assert.Equal(AnomalyKind.Flicker, e.Value.Kind);
            Assert.True(d.InFlicker);
            Assert.InRange(d.LastHz, 8.0, 12.0);
        }

        [Fact]
        public void It_is_reported_once_however_long_it_lasts()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;

            int reports = 0;
            for (int i = 0; i < 2000; i++)
            {
                if (d.Observe(Oscillating(f, 100.0, 0.20, 10.0), t).HasValue) reports++;
                f++; t += Period;
            }

            Assert.Equal(1, reports);
        }

        /// <summary>
        /// The same modulation on a panel a third as bright is the same fault. An amplitude in
        /// luma would catch one camera here and miss another.
        /// </summary>
        [Fact]
        public void A_dim_panel_flickers_at_the_same_threshold_as_a_bright_one()
        {
            var bright = new FlickerDetector(Thresholds());
            var dim = new FlickerDetector(Thresholds());
            long f1 = 1, f2 = 1; double t1 = 0.0, t2 = 0.0;

            Assert.True(Feed(bright, ref f1, ref t1, 400,
                             n => Oscillating(n, 117.0, 0.20, 10.0)).HasValue);
            Assert.True(Feed(dim, ref f2, ref t2, 400,
                             n => Oscillating(n, 35.0, 0.20, 10.0)).HasValue);
        }

        // ----- What it refuses -----

        [Fact]
        public void A_steady_picture_is_never_reported()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 1200, n => Grid(n, 100.0)));
            Assert.False(d.InFlicker);
        }

        /// <summary>
        /// A dropout swings the brightness as far as anything and crosses the mean exactly twice.
        /// Without the crossing test every dropout would arrive here too, under a second name.
        /// </summary>
        [Fact]
        public void A_single_deep_dip_is_a_dropout_and_not_a_flicker()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 200, n => Grid(n, 100.0));

            Feed(d, ref f, ref t, 10, n => Grid(n, 40.0));       // the dip
            AnomalyEvent? e = Feed(d, ref f, ref t, 400, n => Grid(n, 100.0));

            Assert.Null(e);
            Assert.False(d.InFlicker);
        }

        /// <summary>Below the band: a panel dimming over seconds is not oscillating.</summary>
        [Fact]
        public void A_slow_swing_is_out_of_the_band()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 1200, n => Oscillating(n, 100.0, 0.30, 0.5)));
            Assert.False(d.InFlicker);
        }

        /// <summary>
        /// Above the band: alternating every frame is 60 Hz at 120 fps, which is the shape a
        /// backlight running in the kHz leaves behind after sampling. It is an artefact of the
        /// measurement, not something a person could see.
        /// </summary>
        [Fact]
        public void Alternating_every_frame_is_aliasing_and_is_refused()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 1200,
                             n => Grid(n, n % 2 == 0 ? 110.0 : 90.0)));
            Assert.False(d.InFlicker);
            Assert.True(d.LastHz > 25.0);
        }

        /// <summary>An oscillation in the band but too shallow to matter.</summary>
        [Fact]
        public void A_shallow_oscillation_is_under_the_threshold()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;

            Assert.Null(Feed(d, ref f, ref t, 1200, n => Oscillating(n, 100.0, 0.01, 10.0)));
            Assert.False(d.InFlicker);
        }

        [Fact]
        public void A_burst_shorter_than_the_dwell_is_not_reported()
        {
            var t0 = Thresholds();
            t0.EnterMs = 1000.0;                       // 120 frames of confirmed oscillation
            var d = new FlickerDetector(t0);
            long f = 1; double t = 0.0;

            Feed(d, ref f, ref t, 200, n => Grid(n, 100.0));
            Assert.Null(Feed(d, ref f, ref t, 90, n => Oscillating(n, 100.0, 0.20, 10.0)));
        }

        // ----- Darkness -----

        /// <summary>
        /// A relative amplitude on a dark frame is noise over noise. The window is dropped rather
        /// than fed, so a panel switching off does not leave half a second of darkness inside the
        /// next second of picture and read as an enormous swing.
        /// </summary>
        [Fact]
        public void Darkness_empties_the_window_instead_of_filling_it()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 200, n => Grid(n, 100.0));

            Feed(d, ref f, ref t, 30, n => Grid(n, 2.0));        // panel off
            // Straight back to a steady picture: the first window after the dark stretch must be
            // built from the picture alone, not from the step out of darkness.
            Assert.Null(Feed(d, ref f, ref t, 400, n => Grid(n, 100.0)));
            Assert.False(d.InFlicker);
        }

        // ----- The proposal -----

        [Fact]
        public void A_short_run_is_not_enough_to_propose_from()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 300, n => Grid(n, 100.0));

            Assert.False(d.Propose().IsUsable);
            Assert.Contains("NOT ENOUGH", d.Propose().ToString());
        }

        [Fact]
        public void The_proposal_doubles_the_loudest_window_that_was_in_band()
        {
            var d = new FlickerDetector(Thresholds());
            long f = 1; double t = 0.0;
            Feed(d, ref f, ref t, 600, n => Oscillating(n, 100.0, 0.02, 10.0));

            FlickerProposal p = d.Propose();
            Assert.True(p.LoudestAmplitude > 0.0);
            Assert.Equal(p.LoudestAmplitude * 2.0, p.SuggestedEnterAmplitude, 6);
        }

        // ----- Settings -----

        [Fact]
        public void The_exit_is_pulled_under_the_entry_and_the_band_stays_a_band()
        {
            var t = new FlickerThresholds
            {
                EnterAmplitude = 0.05, ExitAmplitude = 0.20, MinHz = 10.0, MaxHz = 5.0,
            };
            t.Normalize();

            Assert.True(t.ExitAmplitude < t.EnterAmplitude);
            Assert.True(t.MaxHz > t.MinHz);
        }

        [Fact]
        public void Settings_round_trip_through_resolve()
        {
            var s = new DetectionSettings();
            KindSettings k = s.For(AnomalyKind.Flicker);
            k.FlickerEnterAmplitude = 0.07;
            k.FlickerExitAmplitude = 0.04;
            k.FlickerMinHz = 4.0;
            k.FlickerMaxHz = 20.0;
            k.FlickerWindowMs = 500.0;
            k.FlickerMinCrossings = 8;
            k.FlickerEnterMs = 1200.0;
            k.FlickerRecoverMs = 800.0;

            FlickerThresholds t = s.ResolveFlicker(AnomalyKind.Flicker, Fps);

            Assert.Equal(0.07, t.EnterAmplitude, 6);
            Assert.Equal(0.04, t.ExitAmplitude, 6);
            Assert.Equal(4.0, t.MinHz, 6);
            Assert.Equal(20.0, t.MaxHz, 6);
            Assert.Equal(60, t.WindowFrames);          // 500 ms at 120 fps
            Assert.Equal(8, t.MinCrossings);
            Assert.Equal(1200.0, t.EnterMs, 6);
            Assert.Equal(800.0, t.RecoverMs, 6);
        }

        /// <summary>
        /// The window is set in milliseconds and used in frames, like every other dwell here - the
        /// same frame count means a different duration at a different exposure.
        /// </summary>
        [Fact]
        public void The_window_is_resolved_from_ms_against_the_measured_rate()
        {
            var s = new DetectionSettings();
            s.For(AnomalyKind.Flicker).FlickerWindowMs = 1000.0;

            Assert.Equal(120, s.ResolveFlicker(AnomalyKind.Flicker, 120.0).WindowFrames);
            Assert.Equal(60, s.ResolveFlicker(AnomalyKind.Flicker, 60.0).WindowFrames);
        }
    }
}
