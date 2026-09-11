using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Splits a detection region into the tile grid <see cref="TileGrid"/> accumulates into.
    ///
    /// The region is whatever rectangle the operator drew, so it almost never divides by eight:
    /// the three regions in use are 284, 306 and 274 pixels wide. Rounding the tile size down and
    /// ignoring the remainder would leave a strip of up to seven pixels on two edges unexamined,
    /// and always the same edges. Spreading the remainder instead gives tiles that differ by one
    /// pixel and cover the region exactly.
    ///
    /// Tiles of unequal size are harmless downstream because TileGrid carries a pixel count per
    /// tile and divides by it, so a 36-pixel tile and a 35-pixel tile yield comparable means.
    /// </summary>
    public static class TileBounds
    {
        /// <summary>
        /// Fills <paramref name="edges"/> with <paramref name="divisions"/> + 1 boundaries across
        /// <paramref name="length"/>, so tile i spans [edges[i], edges[i+1]).
        ///
        /// The first edge is always 0 and the last always <paramref name="length"/>, which is what
        /// makes the cover exact.
        /// </summary>
        public static void Edges(int length, int divisions, int[] edges)
        {
            if (edges == null) throw new ArgumentNullException(nameof(edges));
            if (divisions <= 0) throw new ArgumentOutOfRangeException(nameof(divisions));
            if (edges.Length < divisions + 1)
                throw new ArgumentException("edges must hold divisions + 1 entries", nameof(edges));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

            for (int i = 0; i <= divisions; i++)
                edges[i] = (int)((long)i * length / divisions);
        }

        /// <summary>Allocating form, for callers that are not on a per-frame path.</summary>
        public static int[] Edges(int length, int divisions)
        {
            var edges = new int[divisions + 1];
            Edges(length, divisions, edges);
            return edges;
        }

        /// <summary>
        /// Whether a region this size can be judged tile by tile at all. A region narrower or
        /// shorter than the grid would give empty tiles, and an empty tile is not a dark tile —
        /// letting one through would put a zero into the median of a grid that is merely small.
        /// </summary>
        public static bool CanDivide(int width, int height) =>
            width >= TileGrid.Columns && height >= TileGrid.Rows;
    }
}
