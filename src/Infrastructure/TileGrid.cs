using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// One frame reduced to an 8x8 grid of tiles, each holding a sum, a sum of squares and a pixel
    /// count. Every figure the detector needs derives from those three.
    ///
    /// Tiles rather than one frame-wide statistic, for two reasons the spec pins down:
    /// a fault covering part of the screen disappears into a frame-wide standard deviation — a
    /// quadrant washout most of all — and the frame-to-frame agreement between tiles is the only
    /// thing that separates a screen going dark from content changing. A single global number
    /// throws that away.
    ///
    /// The cost is the same as computing one global statistic: the same pixels are read, only the
    /// accumulator is split 64 ways.
    ///
    /// Free of MIL, because this is the part worth testing and the layer that can be. The reducer
    /// that reads pixels out of a MIL buffer lives on the other side of that line and does nothing
    /// but feed <see cref="Accumulate"/>.
    /// </summary>
    public sealed class TileGrid
    {
        public const int Columns = 8;
        public const int Rows = 8;
        public const int TileCount = Columns * Rows;

        private readonly long[] _sum = new long[TileCount];
        private readonly long[] _sqSum = new long[TileCount];
        private readonly int[] _count = new int[TileCount];

        /// <summary>
        /// Which frame this grid describes. Carried through so the detector can tell a gap in the
        /// numbering — frames the board dropped — from a real change in the picture. Reading a
        /// missed frame's hole as an anomaly is the first false-positive path in this design.
        /// </summary>
        public long FrameNumber { get; set; }

        /// <summary>
        /// Adds one tile's worth of pixels. The reducer accumulates a strip at a time rather than a
        /// pixel at a time: per-pixel virtual calls in the acquisition hook would cost more than the
        /// arithmetic they carry.
        ///
        /// A tile index outside the grid is dropped. The reducer derives indices from geometry, and
        /// an off-by-one there must not corrupt a real tile or throw inside the hook.
        /// </summary>
        public void Accumulate(int tile, long sum, long sqSum, int count)
        {
            if (tile < 0 || tile >= TileCount || count <= 0)
                return;

            _sum[tile] += sum;
            _sqSum[tile] += sqSum;
            _count[tile] += count;
        }

        /// <summary>
        /// Replaces this grid's contents with another's.
        ///
        /// Here because a caller on the acquisition path reuses one grid frame after frame, so
        /// anything that needs to keep a frame has to take a copy of it. Keeping the reference
        /// instead is silent rather than wrong-looking: the kept grid and the next frame become the
        /// same object, every delta between them is zero, and coherence -- which gates the whole
        /// detector -- reads zero forever.
        /// </summary>
        public void CopyFrom(TileGrid source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (ReferenceEquals(source, this)) return;

            Array.Copy(source._sum, _sum, TileCount);
            Array.Copy(source._sqSum, _sqSum, TileCount);
            Array.Copy(source._count, _count, TileCount);
            FrameNumber = source.FrameNumber;
        }

        /// <summary>Whether this tile saw any pixels. An unsampled tile is not a black one.</summary>
        public bool IsPopulated(int tile) =>
            tile >= 0 && tile < TileCount && _count[tile] > 0;

        /// <summary>Mean of the tile, or 0 when it saw nothing.</summary>
        public double Mean(int tile) =>
            IsPopulated(tile) ? (double)_sum[tile] / _count[tile] : 0.0;

        /// <summary>
        /// Population standard deviation within the tile — the flatness that separates a washed-out
        /// screen from a legitimately bright one.
        /// </summary>
        public double Stdev(int tile)
        {
            if (!IsPopulated(tile))
                return 0.0;

            double n = _count[tile];
            double mean = _sum[tile] / n;
            double variance = _sqSum[tile] / n - mean * mean;

            // A flat tile can put the variance a hair below zero in floating point. A NaN escaping
            // here would travel into the washout decision and switch it off without a word.
            return variance <= 0 ? 0.0 : Math.Sqrt(variance);
        }

        /// <summary>
        /// Mean over every pixel the grid saw, pooled rather than averaged across tiles. Averaging
        /// the tile means would let a tile that saw ten pixels count as much as one that saw a
        /// thousand.
        /// </summary>
        public double GlobalMean()
        {
            long sum = 0;
            long count = 0;
            for (int i = 0; i < TileCount; i++)
            {
                sum += _sum[i];
                count += _count[i];
            }
            return count > 0 ? (double)sum / count : 0.0;
        }

        /// <summary>
        /// Median of the populated tiles' means — the frame's representative brightness.
        ///
        /// The median, not the global mean: a bright object crossing the frame, or a popup opening
        /// in a corner, moves a mean and cannot move a median. Unpopulated tiles are left out
        /// rather than counted as zero, or sixty-one empty tiles would drag the answer to nothing.
        /// </summary>
        public double TileMedian()
        {
            Span<double> means = stackalloc double[TileCount];
            int n = 0;
            for (int i = 0; i < TileCount; i++)
                if (_count[i] > 0)
                    means[n++] = (double)_sum[i] / _count[i];

            if (n == 0)
                return 0.0;

            means = means.Slice(0, n);
            means.Sort();

            return (n & 1) == 1
                 ? means[n / 2]
                 : (means[n / 2 - 1] + means[n / 2]) / 2.0;
        }

        /// <summary>
        /// Clears the accumulators so one grid can serve every frame of a channel. Allocating 64
        /// tiles per frame inside the acquisition hook is precisely the work that budget cannot
        /// carry.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_sum, 0, TileCount);
            Array.Clear(_sqSum, 0, TileCount);
            Array.Clear(_count, 0, TileCount);
            FrameNumber = 0;
        }
    }
}
