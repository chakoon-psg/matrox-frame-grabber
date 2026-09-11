using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    public class TileBoundsTests
    {
        [Theory]
        [InlineData(284)]   // CAM0's region width
        [InlineData(306)]   // CAM1
        [InlineData(274)]   // CAM2
        [InlineData(176)]
        [InlineData(200)]
        [InlineData(186)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(1024)]
        public void Edges_CoverTheRegionExactlyAndInOrder(int length)
        {
            int[] edges = TileBounds.Edges(length, TileGrid.Columns);

            Assert.Equal(TileGrid.Columns + 1, edges.Length);
            Assert.Equal(0, edges[0]);
            Assert.Equal(length, edges[edges.Length - 1]);
            for (int i = 1; i < edges.Length; i++)
                Assert.True(edges[i] >= edges[i - 1], $"edge {i} went backwards");
        }

        [Theory]
        [InlineData(284)]
        [InlineData(306)]
        [InlineData(274)]
        [InlineData(9)]
        public void Edges_LeaveNoTileEmptyOnceTheRegionClearsTheGrid(int length)
        {
            int[] edges = TileBounds.Edges(length, TileGrid.Columns);
            for (int i = 0; i < TileGrid.Columns; i++)
                Assert.True(edges[i + 1] - edges[i] > 0, $"tile {i} is empty");
        }

        [Theory]
        [InlineData(284)]
        [InlineData(306)]
        [InlineData(274)]
        [InlineData(1024)]
        public void Edges_SpreadTheRemainderInsteadOfDroppingIt(int length)
        {
            // The point of the whole type: no edge strip goes unexamined, and no tile is more than
            // one pixel off any other.
            int[] edges = TileBounds.Edges(length, TileGrid.Columns);
            int min = int.MaxValue, max = 0, total = 0;
            for (int i = 0; i < TileGrid.Columns; i++)
            {
                int w = edges[i + 1] - edges[i];
                min = Math.Min(min, w);
                max = Math.Max(max, w);
                total += w;
            }
            Assert.Equal(length, total);
            Assert.True(max - min <= 1, $"tiles ranged {min}..{max}");
        }

        [Fact]
        public void Edges_AreEvenWhenTheRegionDividesEvenly()
        {
            int[] edges = TileBounds.Edges(64, 8);
            for (int i = 0; i <= 8; i++)
                Assert.Equal(i * 8, edges[i]);
        }

        [Fact]
        public void Edges_DoNotOverflowOnALargeRegion()
        {
            // (i * length) must not be computed in 32 bits for a wide frame.
            int[] edges = TileBounds.Edges(int.MaxValue, 8);
            Assert.Equal(int.MaxValue, edges[8]);
            for (int i = 1; i <= 8; i++)
                Assert.True(edges[i] > edges[i - 1]);
        }

        [Fact]
        public void Edges_RejectABufferTooSmallToHoldTheBoundaries()
        {
            Assert.Throws<ArgumentException>(() => TileBounds.Edges(284, 8, new int[8]));
        }

        [Fact]
        public void Edges_RejectNonPositiveDivisions()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TileBounds.Edges(284, 0, new int[1]));
        }

        [Theory]
        [InlineData(284, 176, true)]
        [InlineData(8, 8, true)]
        [InlineData(7, 8, false)]
        [InlineData(8, 7, false)]
        [InlineData(0, 0, false)]
        public void CanDivide_RequiresAtLeastOnePixelPerTile(int w, int h, bool expected)
        {
            Assert.Equal(expected, TileBounds.CanDivide(w, h));
        }
    }
}
