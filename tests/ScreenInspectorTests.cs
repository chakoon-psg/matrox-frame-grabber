using System;
using System.IO;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The parts of the inspector slice that carry no ONNX type: the patrol's timing, the
    /// factory's refusals, and the null inspector that makes removal free.
    /// </summary>
    public class ScreenInspectorTests
    {
        // ----- patrol -----

        [Fact]
        public void A_patrol_set_to_zero_never_fires()
        {
            var p = new ScreenPatrol(0.0);

            Assert.False(p.Enabled);
            Assert.False(p.ShouldInspect(1, 0.0));
            Assert.False(p.ShouldInspect(2, 100.0));
            Assert.Equal(0, p.Taken);
        }

        /// <summary>
        /// A run shorter than one interval must still inspect something, or it is
        /// indistinguishable from a run with the inspector switched off.
        /// </summary>
        [Fact]
        public void The_first_frame_is_always_taken()
        {
            var p = new ScreenPatrol(2.0);

            Assert.True(p.ShouldInspect(1, 0.0));
            Assert.Equal(1, p.Taken);
            Assert.Equal(1, p.LastFrame);
        }

        [Fact]
        public void Nothing_more_is_taken_until_the_interval_has_passed()
        {
            var p = new ScreenPatrol(2.0);
            p.ShouldInspect(1, 100.0);

            Assert.False(p.ShouldInspect(2, 100.5));
            Assert.False(p.ShouldInspect(3, 101.9));
            Assert.True(p.ShouldInspect(4, 102.0));
            Assert.Equal(2, p.Taken);
        }

        /// <summary>
        /// At 120 fps and half a hertz, one frame in 240. The rate is what the cost measurement
        /// is a measurement *of*, so it has to be the rate it says it is.
        /// </summary>
        [Fact]
        public void The_rate_is_what_it_says_it_is()
        {
            var p = new ScreenPatrol(2.0);
            const double period = 1.0 / 120.0;

            for (long f = 0; f < 120 * 60; f++)     // one minute of frames
                p.ShouldInspect(f, f * period);

            // 60 s at one every 2 s, plus the frame taken at the start.
            Assert.InRange(p.Taken, 30, 31);
        }

        /// <summary>
        /// A board reset walks the timestamp backwards. Treating that as an enormous interval
        /// would make the patrol fire on every frame until the clock caught up.
        /// </summary>
        [Fact]
        public void A_timestamp_going_backwards_restarts_rather_than_floods()
        {
            var p = new ScreenPatrol(2.0);
            p.ShouldInspect(1, 500.0);

            Assert.True(p.ShouldInspect(2, 0.0));       // restart, taken once
            Assert.False(p.ShouldInspect(3, 0.5));      // and then the interval applies again
            Assert.Equal(2, p.Taken);
        }

        [Fact]
        public void Reset_makes_the_next_frame_the_first_one_again()
        {
            var p = new ScreenPatrol(2.0);
            p.ShouldInspect(1, 0.0);
            p.ShouldInspect(2, 0.5);

            p.Reset();

            Assert.Equal(0, p.Taken);
            Assert.True(p.ShouldInspect(3, 0.6));
        }

        // ----- factory -----

        [Fact]
        public void Switched_off_gives_a_null_inspector_that_says_so()
        {
            IScreenInspector i = ScreenInspectorFactory.Create(false, null, null, null);

            Assert.IsType<NullScreenInspector>(i);
            Assert.Contains("switched off", i.Describe);
        }

        /// <summary>
        /// With `src/Onnx/` deleted nothing registers an implementation, and this is the sentence
        /// that ends up in the log. It is the removal cost being zero, stated.
        /// </summary>
        [Fact]
        public void With_no_implementation_registered_it_says_that_rather_than_failing()
        {
            Func<string, ScreenInspectorOptions, IScreenInspector> saved =
                ScreenInspectorFactory.Implementation;
            try
            {
                ScreenInspectorFactory.Implementation = null;
                IScreenInspector i = ScreenInspectorFactory.Create(true, null, null, null);

                Assert.IsType<NullScreenInspector>(i);
                Assert.Contains("no inspector implementation", i.Describe);
            }
            finally { ScreenInspectorFactory.Implementation = saved; }
        }

        [Fact]
        public void With_no_model_file_it_names_where_it_looked()
        {
            Func<string, ScreenInspectorOptions, IScreenInspector> saved =
                ScreenInspectorFactory.Implementation;
            try
            {
                ScreenInspectorFactory.Implementation = (path, opt) =>
                    throw new InvalidOperationException("should not be reached");

                string empty = Path.Combine(Path.GetTempPath(),
                                            "mfg-inspector-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(empty);
                try
                {
                    IScreenInspector i = ScreenInspectorFactory.Create(true, null, empty, null);

                    Assert.IsType<NullScreenInspector>(i);
                    Assert.Contains(".onnx", i.Describe);
                }
                finally { Directory.Delete(empty, recursive: true); }
            }
            finally { ScreenInspectorFactory.Implementation = saved; }
        }

        /// <summary>
        /// A model that will not load must not stop the app acquiring. The inspector is the
        /// optional judge; the tile detectors are the ones the rig depends on.
        /// </summary>
        [Fact]
        public void A_model_that_throws_on_load_does_not_escape_the_factory()
        {
            Func<string, ScreenInspectorOptions, IScreenInspector> saved =
                ScreenInspectorFactory.Implementation;
            string dir = Path.Combine(Path.GetTempPath(),
                                      "mfg-inspector-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string fake = Path.Combine(dir, "broken.onnx");
                File.WriteAllText(fake, "not a model");

                ScreenInspectorFactory.Implementation =
                    (path, opt) => throw new InvalidDataException("input is [1,?,?,3]");

                IScreenInspector i = ScreenInspectorFactory.Create(true, null, dir, null);

                Assert.IsType<NullScreenInspector>(i);
                Assert.Contains("broken.onnx", i.Describe);
                Assert.Contains("would not load", i.Describe);
            }
            finally
            {
                ScreenInspectorFactory.Implementation = saved;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void A_configured_path_wins_over_the_app_folder()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                                      "mfg-inspector-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string beside = Path.Combine(dir, "beside.onnx");
                string chosen = Path.Combine(dir, "chosen.model");
                File.WriteAllText(beside, "x");
                File.WriteAllText(chosen, "x");

                Assert.Equal(chosen, ScreenInspectorFactory.FindModel(chosen, dir));
                Assert.Equal(beside, ScreenInspectorFactory.FindModel(null, dir));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        // ----- the null inspector -----

        [Fact]
        public void The_null_inspector_finds_nothing_and_reports_nothing()
        {
            using var i = new NullScreenInspector("because");

            var into = new ScreenFinding[4];
            Assert.Equal(0, i.Inspect(new byte[100], into));
            Assert.Empty(i.Reports);
            Assert.Contains("because", i.Describe);
        }

        // ----- the kind the inspector reports -----

        /// <summary>
        /// `Unknown` is the inspector's, and no tile detector will ever produce it. That split is
        /// what lets the catalog stay static while "is an inspector loaded" stays a runtime fact.
        /// </summary>
        [Fact]
        public void Unknown_belongs_to_the_inspector_and_the_rest_to_the_tiles()
        {
            Assert.Equal(KindSource.Inspector, AnomalyCatalog.SourceOf(AnomalyKind.Unknown));
            Assert.False(AnomalyCatalog.Implemented(AnomalyKind.Unknown));

            foreach (AnomalyKind k in AnomalyCatalog.All)
            {
                if (k == AnomalyKind.Unknown) continue;
                Assert.Equal(KindSource.Tiles, AnomalyCatalog.SourceOf(k));
            }
        }

        [Fact]
        public void A_finding_keeps_both_what_we_act_on_and_what_the_model_said()
        {
            var f = new ScreenFinding(AnomalyKind.Unknown, "green", 0.87f, 10f, 20f, 30f, 40f);

            Assert.Equal(AnomalyKind.Unknown, f.Kind);
            Assert.Equal("green", f.Label);
            Assert.Equal(40f, f.Right, 3);
            Assert.Equal(60f, f.Bottom, 3);
            Assert.Equal(1200f, f.Area, 3);
        }
    }
}
