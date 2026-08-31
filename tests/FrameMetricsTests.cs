using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The two figures a dropout decision rests on — how far brightness fell, and whether the tiles
    /// agree about it. Spec section 6.
    /// </summary>
    public class FrameMetricsTests
    {
        /// <summary>A grid where every tile holds one pixel of the given value.</summary>
        static TileGrid Uniform(double value, long frame = 1)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < TileGrid.TileCount; i++)
                g.Accumulate(i, (long)value, (long)(value * value), 1);
            return g;
        }

        /// <summary>A grid built from an explicit list of tile values.</summary>
        static TileGrid From(double[] values, long frame = 1)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < values.Length; i++)
                g.Accumulate(i, (long)values[i], (long)(values[i] * values[i]), 1);
            return g;
        }

        // ----- depth -----

        [Fact]
        public void Depth_IsHowFarTheMedianFellBelowTheBaseline()
        {
            // 120 against a baseline of 150 is a fifth of the way down.
            Assert.Equal(0.20, FrameMetrics.Depth(120, 150), 6);
        }

        [Fact]
        public void Depth_IsOneWhenTheScreenIsFullyDark()
        {
            Assert.Equal(1.0, FrameMetrics.Depth(0, 150), 6);
        }

        [Fact]
        public void Depth_IsZeroForAFrameBrighterThanTheBaseline()
        {
            // Brighter than usual is not a dropout. A negative depth would compare against
            // thresholds in the wrong direction.
            Assert.Equal(0.0, FrameMetrics.Depth(200, 150), 6);
        }

        [Fact]
        public void Depth_IsZeroWhenThereIsNoBaselineYet()
        {
            // Before the running median has anything in it, every frame would otherwise read as a
            // total blackout.
            Assert.Equal(0.0, FrameMetrics.Depth(120, 0), 6);
        }

        // ----- coherence -----

        [Fact]
        public void Coherence_IsOneWhenEveryTileMovesTheSameWay()
        {
            // A screen going dark: all 64 tiles fall together. This is the signature the gate exists
            // to recognise.
            double coh = FrameMetrics.Coherence(Uniform(150, 1), Uniform(90, 2));

            Assert.Equal(1.0, coh, 6);
        }

        [Fact]
        public void Coherence_IsZeroWhenTilesMoveOppositeAndEqually()
        {
            // Content changing: half the frame brightens as half darkens. The sum cancels while the
            // magnitudes do not.
            var before = From(new double[] { 100, 100, 100, 100 }, 1);
            var after  = From(new double[] { 120, 80, 120, 80 }, 2);

            Assert.Equal(0.0, FrameMetrics.Coherence(before, after), 6);
        }

        [Fact]
        public void Coherence_IsLowForAScrollingPictureWhereTilesDisagree()
        {
            // A map panning: tiles move by different amounts in different directions. Well under a
            // gate of 0.8.
            var before = From(new double[] { 100, 100, 100, 100, 100, 100 }, 1);
            var after  = From(new double[] { 130, 70, 110, 85, 125, 80 }, 2);

            Assert.True(FrameMetrics.Coherence(before, after) < 0.5);
        }

        [Fact]
        public void Coherence_SeesAPartialBlankAsCoherent()
        {
            // A quadrant goes dark and the rest holds still. The tiles that moved all moved one
            // way, so this must not be mistaken for content.
            var before = From(new double[] { 100, 100, 100, 100 }, 1);
            var after  = From(new double[] { 0, 100, 100, 100 }, 2);

            Assert.Equal(1.0, FrameMetrics.Coherence(before, after), 6);
        }

        [Fact]
        public void Coherence_IsZeroForAStillPictureRatherThanUndefined()
        {
            // Nothing moved: the denominator is zero. Returning 1 here would pair with any depth
            // that noise produced and fire on a completely static screen.
            Assert.Equal(0.0, FrameMetrics.Coherence(Uniform(100, 1), Uniform(100, 2)), 6);
        }

        [Fact]
        public void Coherence_OnlyComparesTilesPopulatedInBothFrames()
        {
            // The sampled region can change between frames — the operator redraws the ROI. A tile
            // present in one frame and absent in the other has no delta, not a delta of its value.
            var before = From(new double[] { 100, 100 }, 1);
            var after  = From(new double[] { 90, 90, 90, 90 }, 2);

            Assert.Equal(1.0, FrameMetrics.Coherence(before, after), 6);
        }

        [Fact]
        public void Coherence_OfNothingIsZero()
        {
            Assert.Equal(0.0, FrameMetrics.Coherence(new TileGrid(), new TileGrid()), 6);
        }

        [Fact]
        public void Coherence_IgnoresANullGrid()
        {
            // The first frame of a grab has no predecessor.
            Assert.Equal(0.0, FrameMetrics.Coherence(null, Uniform(100, 1)), 6);
        }
    }
}
