using System;
using System.Collections.Generic;
using System.Diagnostics;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Turns one grabbed frame into the 8x8 <see cref="TileGrid"/> the detector judges.
    ///
    /// This runs inside the acquisition hook, which is the one place a grab buffer is guaranteed
    /// not to be rewritten underneath the read. Everything about the implementation follows from
    /// that: the reads are the smallest that answer the question, the arithmetic is integer, and
    /// nothing is allocated per frame.
    ///
    /// Cost is measured rather than assumed. <see cref="LastReduceUs"/> and
    /// <see cref="MaxReduceUs"/> are reported on the stats tick beside the missed-frame counter,
    /// because the acceptance test for putting this on the acquisition path at all is that missed
    /// frames stay at zero -- and the baseline to compare against was measured before this
    /// existed: 8000 us exposure, 124.3 fps, three channels, nothing lost.
    ///
    /// Band children are bound to the grab ring once, not created per frame. MbufChildColor is a
    /// MIL allocation, and three channels at 124 fps would otherwise ask for 1100 of them a
    /// second. They are freed in <see cref="Unbind"/>, which the channel calls before freeing the
    /// ring -- a child outliving its parent is a failure this codebase has already paid for.
    /// </summary>
    public sealed class TileReducer
    {
        /// <summary>Rec.601 weights, scaled to integers so the per-pixel path needs no float.</summary>
        private const int LumaR = 299, LumaG = 587, LumaB = 114, LumaScale = 1000;

        private readonly int[] _columnEdges = new int[TileGrid.Columns + 1];
        private readonly int[] _rowEdges = new int[TileGrid.Rows + 1];

        private byte[] _r = Array.Empty<byte>();
        private byte[] _g = Array.Empty<byte>();
        private byte[] _b = Array.Empty<byte>();

        private int _planWidth, _planHeight;

        // Band children, keyed by the grab buffer they belong to.
        private readonly Dictionary<MIL_ID, MIL_ID[]> _bands = new Dictionary<MIL_ID, MIL_ID[]>();
        private bool _bandsUsable;

        private readonly Stopwatch _watch = new Stopwatch();

        /// <summary>Microseconds the last reduction took.</summary>
        public double LastReduceUs { get; private set; }

        /// <summary>
        /// Worst reduction since the last <see cref="ResetCost"/>. Read together with
        /// <see cref="MeanReduceUs"/>, never alone: measured over 7461 frames the maximum was set
        /// six times and the mean was a fraction of it, so the maximum on its own reads as a cost
        /// the reduction does not actually carry.
        /// </summary>
        public double MaxReduceUs { get; private set; }

        /// <summary>Mean reduction over this run, which is the cost actually being paid.</summary>
        public double MeanReduceUs => _reductions > 0 ? _totalReduceUs / _reductions : 0.0;

        /// <summary>
        /// Reductions attempted this run, the denominator of the mean. Counted in the finally, so
        /// it includes the ones that produced nothing -- compare it with <see cref="Accepted"/>.
        /// </summary>
        public long Reductions => _reductions;

        /// <summary>
        /// Reductions that produced a grid. A gap between this and <see cref="Reductions"/> is the
        /// failure mode that has to be visible: an early return leaves the detector fed nothing at
        /// all while every other counter looks healthy, which is how a silent detector first
        /// showed up here.
        /// </summary>
        public long Accepted => _accepted;

        private double _totalReduceUs;
        private long _reductions;
        private long _accepted;

        /// <summary>Consecutive MIL failures. Non-zero means the detector is being fed nothing.</summary>
        public int ConsecutiveFailures { get; private set; }

        /// <summary>
        /// Whether to carry per-band totals alongside luma. Off unless a kind asks for it.
        ///
        /// The three bytes are already in registers - the luma line reads all of them - so the
        /// cost is three adds per pixel and nothing else. It is still a flag rather than always-on
        /// because this loop runs inside the acquisition budget, and the rule in this repo is that
        /// a kind nobody enabled costs nothing (turning every kind off skips the reduction
        /// entirely). Measure the reduce time rather than trusting the paragraph.
        /// </summary>
        public bool WantBands { get; set; }

        // Whole-region planes, separate from the strip buffers above.
        //
        // The reduction reads a tile row at a time and reuses one strip, which is why _r/_g/_b
        // cannot simply be handed out - by the time Reduce returns they hold the last strip only.
        // An inspector needs the whole region at once, so it gets its own arrays, allocated the
        // first time one is asked for and never on a frame that does not ask.
        private byte[] _roiR, _roiG, _roiB;
        private int _roiW, _roiH;

        /// <summary>
        /// Reads one whole region out of the grab buffer as three band planes.
        ///
        /// **This is a second host read of the same frame**, which this repository measured as
        /// expensive and forbade: two sinks reading every frame made each read four times slower,
        /// not twice, because they contend for the same memory path the acquisition DMA uses.
        ///
        /// It is allowed here because of how rarely it happens. The patrol asks for one frame
        /// every two seconds per camera - one in 240 at 120 fps, 0.4% of them. The rule is about
        /// what every frame costs; this is not on that path. If something ever wants this on
        /// every frame, it has to share the reduction's read instead.
        ///
        /// Returns false when there is nothing to read, in which case the planes are untouched.
        /// Never throws: this runs inside the acquisition hook.
        /// </summary>
        public bool TryReadRoi(MIL_ID grabBuffer, ChannelRoi roi,
                               out byte[] r, out byte[] g, out byte[] b, out int width, out int height)
        {
            r = null; g = null; b = null; width = 0; height = 0;
            if (grabBuffer == MIL.M_NULL) return false;

            try
            {
                if (!_bandsUsable || !_bands.TryGetValue(grabBuffer, out MIL_ID[] children))
                    return false;
                if (MIL.MbufInquire(grabBuffer, MIL.M_SIZE_BIT, MIL.M_NULL) > 8)
                    return false;

                MIL_INT fw = MIL.MbufInquire(grabBuffer, MIL.M_SIZE_X, MIL.M_NULL);
                MIL_INT fh = MIL.MbufInquire(grabBuffer, MIL.M_SIZE_Y, MIL.M_NULL);

                bool full = roi.IsFullFrame;
                int rx = full ? 0 : roi.OffsetX;
                int ry = full ? 0 : roi.OffsetY;
                int rw = full ? (int)fw : roi.Width;
                int rh = full ? (int)fh : roi.Height;

                if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0 ||
                    rx + rw > (int)fw || ry + rh > (int)fh)
                    return false;

                if (_roiR == null || _roiW != rw || _roiH != rh)
                {
                    _roiR = new byte[rw * rh];
                    _roiG = new byte[rw * rh];
                    _roiB = new byte[rw * rh];
                    _roiW = rw;
                    _roiH = rh;
                }

                // MbufGet2d for the same reason the reduction uses it: these buffers carry row
                // padding, and a padded read into a tight array shears every row.
                MIL.MbufGet2d(children[0], rx, ry, rw, rh, _roiR);
                MIL.MbufGet2d(children[1], rx, ry, rw, rh, _roiG);
                MIL.MbufGet2d(children[2], rx, ry, rw, rh, _roiB);

                r = _roiR; g = _roiG; b = _roiB;
                width = rw; height = rh;
                return true;
            }
            catch (MILException)
            {
                return false;
            }
        }

        public void ResetCost()
        {
            LastReduceUs = 0;
            MaxReduceUs = 0;
            _totalReduceUs = 0;
            _reductions = 0;
            _accepted = 0;
        }

        /// <summary>
        /// Allocates one set of band children per grab buffer. Safe to call on a single-band ring:
        /// nothing is allocated and <see cref="Reduce"/> takes the mono path.
        /// </summary>
        public void Bind(IList<MIL_ID> grabBuffers)
        {
            Unbind();
            if (grabBuffers == null || grabBuffers.Count == 0)
                return;

            try
            {
                if (MIL.MbufInquire(grabBuffers[0], MIL.M_SIZE_BAND, MIL.M_NULL) < 3)
                    return;   // mono ring (RAW recording); the mono path needs no children

                foreach (MIL_ID buffer in grabBuffers)
                {
                    if (buffer == MIL.M_NULL) continue;

                    var children = new MIL_ID[3];
                    for (int band = 0; band < 3; band++)
                    {
                        MIL_ID child = MIL.M_NULL;
                        MIL.MbufChildColor(buffer, band, ref child);
                        children[band] = child;
                    }
                    _bands[buffer] = children;
                }
                _bandsUsable = true;
            }
            catch (MILException e)
            {
                // Fall back to the mono path rather than losing detection entirely: one band of a
                // colour frame still tracks a screen going dark, and depth is a ratio.
                MilErrorLog.Write("tile reducer: binding band children", e);
                Unbind();
            }
        }

        /// <summary>Frees the band children. Must run before the grab ring is freed.</summary>
        public void Unbind()
        {
            foreach (MIL_ID[] children in _bands.Values)
            {
                for (int band = children.Length - 1; band >= 0; band--)
                {
                    if (children[band] == MIL.M_NULL) continue;
                    try { MIL.MbufFree(children[band]); }
                    catch (MILException) { /* the ring is going away regardless */ }
                }
            }
            _bands.Clear();
            _bandsUsable = false;
        }

        /// <summary>
        /// Reduces <paramref name="roi"/> of <paramref name="grabBuffer"/> into
        /// <paramref name="grid"/>, which is reset first. Returns false when there was nothing to
        /// read, in which case the grid is left empty and the caller must not judge it.
        ///
        /// Never throws: a failure here must not take down the acquisition hook.
        /// </summary>
        public bool Reduce(MIL_ID grabBuffer, ChannelRoi roi, TileGrid grid)
        {
            if (grabBuffer == MIL.M_NULL || grid == null)
                return false;

            _watch.Restart();
            try
            {
                MIL_INT width = MIL.MbufInquire(grabBuffer, MIL.M_SIZE_X, MIL.M_NULL);
                MIL_INT height = MIL.MbufInquire(grabBuffer, MIL.M_SIZE_Y, MIL.M_NULL);

                // A deeper buffer read as bytes would hand back the high byte only, pinning every
                // tile near 255 and reporting a permanently bright screen. Refusing to judge is
                // the honest answer; BrightnessMeter refuses on the same grounds.
                if (MIL.MbufInquire(grabBuffer, MIL.M_SIZE_BIT, MIL.M_NULL) > 8)
                    return false;

                bool full = roi.IsFullFrame;
                int rx = full ? 0 : roi.OffsetX;
                int ry = full ? 0 : roi.OffsetY;
                int rw = full ? (int)width : roi.Width;
                int rh = full ? (int)height : roi.Height;

                if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0 ||
                    rx + rw > (int)width || ry + rh > (int)height ||
                    !TileBounds.CanDivide(rw, rh))
                    return false;

                EnsurePlan(rw, rh);
                grid.Reset();

                MIL_ID[] children = null;
                bool colour = _bandsUsable && _bands.TryGetValue(grabBuffer, out children);

                for (int tileRow = 0; tileRow < TileGrid.Rows; tileRow++)
                {
                    int y0 = _rowEdges[tileRow];
                    int rows = _rowEdges[tileRow + 1] - y0;
                    if (rows <= 0) continue;

                    if (colour)
                    {
                        // MbufGet2d, not MbufGet: these buffers carry row padding, and a padded
                        // read into a tight array shears the rows. It is also the only form that
                        // takes an X offset, which the region needs.
                        MIL.MbufGet2d(children[0], rx, ry + y0, rw, rows, _r);
                        MIL.MbufGet2d(children[1], rx, ry + y0, rw, rows, _g);
                        MIL.MbufGet2d(children[2], rx, ry + y0, rw, rows, _b);
                        AccumulateColour(grid, tileRow, rw, rows);
                    }
                    else
                    {
                        MIL.MbufGet2d(grabBuffer, rx, ry + y0, rw, rows, _r);
                        AccumulateMono(grid, tileRow, rw, rows);
                    }
                }

                ConsecutiveFailures = 0;
                _accepted++;
                return true;
            }
            catch (MILException)
            {
                // A ring can be freed and reallocated underneath a hook that is still draining.
                // Losing one frame's grid costs one frame of detection; counting the failures is
                // what stops a permanently blind detector from looking like a quiet one.
                ConsecutiveFailures++;
                return false;
            }
            finally
            {
                _watch.Stop();
                LastReduceUs = _watch.Elapsed.TotalMilliseconds * 1000.0;
                if (LastReduceUs > MaxReduceUs) MaxReduceUs = LastReduceUs;
                _totalReduceUs += LastReduceUs;
                _reductions++;
            }
        }

        private void AccumulateColour(TileGrid grid, int tileRow, int rw, int rows)
        {
            int tileBase = tileRow * TileGrid.Columns;
            for (int column = 0; column < TileGrid.Columns; column++)
            {
                int x0 = _columnEdges[column];
                int x1 = _columnEdges[column + 1];
                if (x1 <= x0) continue;

                long sum = 0, sqSum = 0;
                int count = 0;
                if (WantBands)
                {
                    long bandR = 0, bandG = 0, bandB = 0;
                    for (int row = 0; row < rows; row++)
                    {
                        int offset = row * rw;
                        for (int x = x0; x < x1; x++)
                        {
                            int i = offset + x;
                            int r = _r[i], g = _g[i], b = _b[i];
                            int luma = (LumaR * r + LumaG * g + LumaB * b) / LumaScale;
                            sum += luma;
                            sqSum += (long)luma * luma;
                            bandR += r;
                            bandG += g;
                            bandB += b;
                            count++;
                        }
                    }
                    grid.AccumulateBands(bandR, bandG, bandB, count);
                }
                else
                {
                    for (int row = 0; row < rows; row++)
                    {
                        int offset = row * rw;
                        for (int x = x0; x < x1; x++)
                        {
                            int i = offset + x;
                            int luma = (LumaR * _r[i] + LumaG * _g[i] + LumaB * _b[i]) / LumaScale;
                            sum += luma;
                            sqSum += (long)luma * luma;
                            count++;
                        }
                    }
                }
                grid.Accumulate(tileBase + column, sum, sqSum, count);
            }
        }

        private void AccumulateMono(TileGrid grid, int tileRow, int rw, int rows)
        {
            // RAW recording turns M_BAYER_CONVERSION off, so the ring becomes the single-band
            // Bayer mosaic. Its plain mean is 0.25R + 0.50G + 0.25B by the CFA's 1R:2G:1B ratio,
            // close enough to Rec.601 that depth stays comparable across the switch.
            int tileBase = tileRow * TileGrid.Columns;
            for (int column = 0; column < TileGrid.Columns; column++)
            {
                int x0 = _columnEdges[column];
                int x1 = _columnEdges[column + 1];
                if (x1 <= x0) continue;

                long sum = 0, sqSum = 0;
                int count = 0;
                for (int row = 0; row < rows; row++)
                {
                    int offset = row * rw;
                    for (int x = x0; x < x1; x++)
                    {
                        int v = _r[offset + x];
                        sum += v;
                        sqSum += (long)v * v;
                        count++;
                    }
                }
                grid.Accumulate(tileBase + column, sum, sqSum, count);
            }
        }

        /// <summary>
        /// Recomputes the tile edges and read buffers, only when the region changed: the operator
        /// can redraw it mid-grab, and this is called per frame.
        /// </summary>
        private void EnsurePlan(int width, int height)
        {
            if (_planWidth == width && _planHeight == height)
                return;

            TileBounds.Edges(width, TileGrid.Columns, _columnEdges);
            TileBounds.Edges(height, TileGrid.Rows, _rowEdges);

            // One read per band per tile row, so the strip has to hold the tallest tile row.
            int tallest = 0;
            for (int row = 0; row < TileGrid.Rows; row++)
                tallest = Math.Max(tallest, _rowEdges[row + 1] - _rowEdges[row]);

            int needed = width * tallest;
            if (_r.Length < needed)
            {
                _r = new byte[needed];
                _g = new byte[needed];
                _b = new byte[needed];
            }

            _planWidth = width;
            _planHeight = height;
        }
    }
}
