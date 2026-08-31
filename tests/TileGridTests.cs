using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// One frame reduced to 8x8 tiles. Everything the detector needs comes off this — the spec's
    /// section 3 — and it is pure arithmetic, so it is checkable without a board or a camera.
    /// </summary>
    public class TileGridTests
    {
        [Fact]
        public void Mean_IsTheTilesSumOverItsCount()
        {
            var g = new TileGrid();
            g.Accumulate(0, sum: 400, sqSum: 40000, count: 4);

            Assert.Equal(100.0, g.Mean(0), 6);
        }

        [Fact]
        public void Stdev_IsZeroForAUniformTile()
        {
            // Four pixels all at 100: sum 400, sum of squares 4 * 100^2.
            var g = new TileGrid();
            g.Accumulate(0, sum: 400, sqSum: 40000, count: 4);

            Assert.Equal(0.0, g.Stdev(0), 6);
        }

        [Fact]
        public void Stdev_MeasuresSpreadWithinTheTile()
        {
            // Values 90, 110: mean 100, population stdev 10.
            var g = new TileGrid();
            g.Accumulate(0, sum: 200, sqSum: 90 * 90 + 110 * 110, count: 2);

            Assert.Equal(10.0, g.Stdev(0), 6);
        }

        [Fact]
        public void Stdev_IsNeverNaNWhenRoundingPushesTheVarianceNegative()
        {
            // sq/n - mean^2 can land just below zero in floating point on a flat tile. A NaN here
            // would propagate into the washout decision and disable it silently.
            var g = new TileGrid();
            g.Accumulate(0, sum: 3 * 85, sqSum: 3 * 85 * 85, count: 3);

            Assert.Equal(0.0, g.Stdev(0), 6);
        }

        [Fact]
        public void EmptyTile_ReportsNoMeanRatherThanDividingByZero()
        {
            var g = new TileGrid();

            Assert.Equal(0.0, g.Mean(5), 6);
            Assert.Equal(0.0, g.Stdev(5), 6);
            Assert.False(g.IsPopulated(5));
        }

        [Fact]
        public void GlobalMean_PoolsEveryTileRatherThanAveragingTheMeans()
        {
            // Tile 0 sees 1000 pixels at 100, tile 1 sees 10 at 200. Pooling gives 100.99;
            // averaging the two means would give 150 and let a tiny tile dominate.
            var g = new TileGrid();
            g.Accumulate(0, sum: 1000 * 100, sqSum: 1000 * 100 * 100, count: 1000);
            g.Accumulate(1, sum: 10 * 200, sqSum: 10 * 200 * 200, count: 10);

            Assert.Equal(100.99, g.GlobalMean(), 2);
        }

        [Fact]
        public void TileMedian_IsTheMiddleOfThePopulatedTileMeans()
        {
            // The representative value is the median, not the global mean, so a bright object
            // crossing the frame cannot drag it — spec section 6.
            var g = new TileGrid();
            g.Accumulate(0, 100, 100 * 100, 1);
            g.Accumulate(1, 110, 110 * 110, 1);
            g.Accumulate(2, 255, 255 * 255, 1);   // the bright intruder

            Assert.Equal(110.0, g.TileMedian(), 6);
        }

        [Fact]
        public void TileMedian_IgnoresTilesThatSawNoPixels()
        {
            // 61 empty tiles must not pull the median to zero.
            var g = new TileGrid();
            g.Accumulate(0, 100, 100 * 100, 1);
            g.Accumulate(1, 120, 120 * 120, 1);
            g.Accumulate(2, 140, 140 * 140, 1);

            Assert.Equal(120.0, g.TileMedian(), 6);
        }

        [Fact]
        public void TileMedian_AveragesTheTwoMiddlesForAnEvenCount()
        {
            var g = new TileGrid();
            g.Accumulate(0, 100, 100 * 100, 1);
            g.Accumulate(1, 120, 120 * 120, 1);

            Assert.Equal(110.0, g.TileMedian(), 6);
        }

        [Fact]
        public void TileMedian_OfNothingIsZeroRatherThanAThrow()
        {
            Assert.Equal(0.0, new TileGrid().TileMedian(), 6);
        }

        [Fact]
        public void Reset_ClearsTheAccumulatorsSoAGridCanBeReused()
        {
            // The reducer reuses one grid per channel: allocating 64 tiles per frame inside the
            // acquisition hook is exactly the kind of work that budget cannot afford.
            var g = new TileGrid();
            g.Accumulate(0, 400, 40000, 4);
            g.FrameNumber = 7;

            g.Reset();

            Assert.False(g.IsPopulated(0));
            Assert.Equal(0.0, g.TileMedian(), 6);
            Assert.Equal(0, g.FrameNumber);
        }

        [Fact]
        public void Grid_IsEightByEight()
        {
            Assert.Equal(8, TileGrid.Columns);
            Assert.Equal(8, TileGrid.Rows);
            Assert.Equal(64, TileGrid.TileCount);
        }

        [Fact]
        public void Accumulate_IgnoresATileIndexOutsideTheGrid()
        {
            // The reducer computes indices from geometry; a bad one must not corrupt a real tile
            // or throw inside the acquisition hook.
            var g = new TileGrid();
            g.Accumulate(-1, 400, 40000, 4);
            g.Accumulate(64, 400, 40000, 4);

            Assert.Equal(0.0, g.TileMedian(), 6);
        }
    }
}
