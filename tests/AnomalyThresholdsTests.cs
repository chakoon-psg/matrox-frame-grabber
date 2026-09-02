using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// These arrive from a settings file a person edits, so every one of them is clamped on the way
    /// in. The failure to guard against is not a crash: each of these values has a setting that
    /// silences the detector while leaving it looking like it is running, which on a rig watching
    /// for faults is the worst outcome available.
    /// </summary>
    public class AnomalyThresholdsTests
    {
        static AnomalyThresholds Source() => new AnomalyThresholds
        {
            Depth = 0.15,
            Coherence = 0.70,
            DebounceFrames = 12,
            MaxEventFrames = 300,
            BaselineWindow = 61,
            BaselineWarmupFrames = 20,
        };

        [Fact]
        public void CopyFrom_TakesEveryThreshold()
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(Source());

            Assert.Equal(0.15, target.Depth, 6);
            Assert.Equal(0.70, target.Coherence, 6);
            Assert.Equal(12, target.DebounceFrames);
            Assert.Equal(300, target.MaxEventFrames);
            Assert.Equal(61, target.BaselineWindow);
            Assert.Equal(20, target.BaselineWarmupFrames);
        }

        [Fact]
        public void CopyFrom_IgnoresNullSoAMissingSectionKeepsTheDefaults()
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(null);
            Assert.Equal(AnomalyThresholds.DefaultDepth, target.Depth, 6);
        }

        [Theory]
        [InlineData(0.0)]      // fires on every frame
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void CopyFrom_RefusesADepthThatWouldFireOnEverything(double depth)
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(new AnomalyThresholds { Depth = depth });
            Assert.True(target.Depth > 0, $"depth came out {target.Depth}");
        }

        [Theory]
        [InlineData(1.0)]      // nothing can fall the whole way and still be measured
        [InlineData(5.0)]
        public void CopyFrom_RefusesADepthThatWouldFireOnNothing(double depth)
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(new AnomalyThresholds { Depth = depth });
            Assert.True(target.Depth < 1.0, $"depth came out {target.Depth}");
        }

        [Theory]
        [InlineData(1.5, 1.0)]     // a gate above 1 can never be satisfied: nothing would ever fire
        [InlineData(-0.5, 0.0)]
        public void CopyFrom_KeepsCoherenceInsideZeroToOne(double given, double expected)
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(new AnomalyThresholds { Coherence = given });
            Assert.Equal(expected, target.Coherence, 6);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void CopyFrom_KeepsFrameCountsPositive(int given)
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(new AnomalyThresholds
            {
                DebounceFrames = given,
                MaxEventFrames = given,
                BaselineWarmupFrames = given,
            });

            Assert.True(target.DebounceFrames >= 1);
            Assert.True(target.MaxEventFrames >= 1);
            Assert.True(target.BaselineWarmupFrames >= 1);
        }

        [Fact]
        public void CopyFrom_KeepsTheBaselineWindowBigEnoughToHaveAMedian()
        {
            var target = new AnomalyThresholds();
            target.CopyFrom(new AnomalyThresholds { BaselineWindow = 1 });
            Assert.True(target.BaselineWindow >= 3, $"window came out {target.BaselineWindow}");
        }

        [Fact]
        public void ADetectorBuiltFromClampedThresholdsStillFindsADropout()
        {
            // The clamps have to leave something that works, not merely something that parses.
            var thresholds = new AnomalyThresholds();
            thresholds.CopyFrom(new AnomalyThresholds
            {
                Depth = 0.0,           // clamped up
                Coherence = 9.0,       // clamped to 1.0 - the strictest gate that can be met
                DebounceFrames = 0,
                MaxEventFrames = 0,
                BaselineWindow = 0,
                BaselineWarmupFrames = 0,
            });

            var detector = new AnomalyDetector(thresholds);
            for (long n = 1; n <= 20; n++)
            {
                var grid = new TileGrid { FrameNumber = n };
                for (int i = 0; i < TileGrid.TileCount; i++) grid.Accumulate(i, 150, 150 * 150, 1);
                detector.Observe(grid, n * 0.01);
            }

            var dark = new TileGrid { FrameNumber = 21 };
            for (int i = 0; i < TileGrid.TileCount; i++) dark.Accumulate(i, 10, 100, 1);
            detector.Observe(dark, 0.21);

            Assert.True(detector.LastDepth > 0.5, $"depth was {detector.LastDepth}");
        }
    }
}
