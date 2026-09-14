using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Turns an acquired planar frame into the interleaved BGR block a model takes, and turns
    /// model coordinates back into frame coordinates.
    ///
    /// Two jobs in one class on purpose: **whatever shrank the picture is the only thing that
    /// knows how to undo it.** Splitting them is how the nominal scale ends up being used for the
    /// inverse, which is the bug this class was written twice to avoid (see <see cref="ScaleY"/>).
    ///
    /// **Planar in, interleaved out.** Our frames are gbrp - three separate band planes, because
    /// that is what ffmpeg takes and what MIL hands over without a repack. Models take interleaved
    /// BGR. So the de-planarisation and the shrink happen in one pass; there is no intermediate.
    ///
    /// **No OpenCV.** It would be used here and nowhere else, for 93.7 MB - of which 27.3 MB is a
    /// second copy of ffmpeg in an app that already finds ffmpeg.exe at runtime. The cs project's
    /// own measurements closed this: preprocessing plus decoding together are under 2% of an
    /// inference (0.13 ms at the top), below the run-to-run spread. There is no performance to
    /// lose by writing it here, and a dependency to avoid.
    /// </summary>
    public sealed class BgrLetterbox
    {
        private readonly int _outW, _outH;
        private readonly int _srcW, _srcH;
        private readonly byte[] _out;

        // Per-destination-column source spans, computed once. The inner loop then reads them
        // instead of recomputing a division per pixel.
        private readonly int[] _colStart, _colEnd;
        private readonly int[] _rowStart, _rowEnd;

        /// <summary>
        /// Fixes the mapping for one frame size and one model size.
        ///
        /// Both are fixed for the life of a grab, so everything derived is computed here rather
        /// than per frame.
        /// </summary>
        public BgrLetterbox(int srcWidth, int srcHeight, int modelWidth, int modelHeight)
        {
            if (srcWidth <= 0 || srcHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(srcWidth), "frame size must be positive");
            if (modelWidth <= 0 || modelHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(modelWidth), "model size must be positive");

            _srcW = srcWidth;
            _srcH = srcHeight;
            _outW = modelWidth;
            _outH = modelHeight;

            Scale = Math.Min((double)modelWidth / srcWidth, (double)modelHeight / srcHeight);
            FittedWidth = (int)Math.Round(srcWidth * Scale);
            FittedHeight = (int)Math.Round(srcHeight * Scale);
            if (FittedWidth < 1) FittedWidth = 1;
            if (FittedHeight < 1) FittedHeight = 1;
            if (FittedWidth > modelWidth) FittedWidth = modelWidth;
            if (FittedHeight > modelHeight) FittedHeight = modelHeight;

            // Padding can be uneven: 256 - 241 = 15 puts 7 above and 8 below. So the picture's
            // centre is not the frame's centre, and a test that checks the centre proves nothing.
            // Check the edges.
            PadX = (modelWidth - FittedWidth) / 2;
            PadY = (modelHeight - FittedHeight) / 2;

            // The ratio the fill actually samples at, which is not Scale once the fitted size has
            // been rounded to an integer. See ScaleY.
            ScaleX = (double)FittedWidth / srcWidth;
            ScaleY = (double)FittedHeight / srcHeight;

            _out = new byte[modelWidth * modelHeight * 3];

            _colStart = new int[FittedWidth];
            _colEnd = new int[FittedWidth];
            for (int i = 0; i < FittedWidth; i++)
            {
                _colStart[i] = (int)((long)i * srcWidth / FittedWidth);
                _colEnd[i] = (int)((long)(i + 1) * srcWidth / FittedWidth);
                if (_colEnd[i] <= _colStart[i]) _colEnd[i] = _colStart[i] + 1;
                if (_colEnd[i] > srcWidth) _colEnd[i] = srcWidth;
            }

            _rowStart = new int[FittedHeight];
            _rowEnd = new int[FittedHeight];
            for (int i = 0; i < FittedHeight; i++)
            {
                _rowStart[i] = (int)((long)i * srcHeight / FittedHeight);
                _rowEnd[i] = (int)((long)(i + 1) * srcHeight / FittedHeight);
                if (_rowEnd[i] <= _rowStart[i]) _rowEnd[i] = _rowStart[i] + 1;
                if (_rowEnd[i] > srcHeight) _rowEnd[i] = srcHeight;
            }
        }

        /// <summary>
        /// The nominal scale, `min(outW/srcW, outH/srcH)`.
        ///
        /// **Do not use this to invert a coordinate.** It is the ratio before the fitted size was
        /// rounded to whole pixels, and after rounding the fill samples at a slightly different
        /// one. Measured on the real geometry: 1024x772 into 320x256 gives Scale 0.3125 and a
        /// fitted height of round(241.25) = 241, so the fill samples at 241/772 = 0.31218.
        /// Inverting the frame's bottom edge with Scale gives 771.2 where 772 is correct - every
        /// coordinate off by up to 1.2 px, in a feature whose output is coordinates. Use
        /// <see cref="ScaleX"/> and <see cref="ScaleY"/>.
        /// </summary>
        public double Scale { get; }

        /// <summary>The ratio the fill samples horizontally. Use this to invert x.</summary>
        public double ScaleX { get; }

        /// <summary>The ratio the fill samples vertically. Use this to invert y.</summary>
        public double ScaleY { get; }

        /// <summary>The frame width this instance was built for.</summary>
        public int SourceWidth => _srcW;

        /// <summary>The frame height this instance was built for.</summary>
        public int SourceHeight => _srcH;

        /// <summary>Width of the picture inside the letterbox.</summary>
        public int FittedWidth { get; }

        /// <summary>Height of the picture inside the letterbox.</summary>
        public int FittedHeight { get; }

        /// <summary>Padding on the left. The right may be one more - see the constructor.</summary>
        public int PadX { get; }

        /// <summary>Padding on the top. The bottom may be one more.</summary>
        public int PadY { get; }

        /// <summary>The interleaved BGR block, reused every call. Not thread safe.</summary>
        public byte[] Buffer => _out;

        /// <summary>
        /// Fills the buffer from three band planes.
        ///
        /// **Box average, not bilinear.** 1024 to 320 is a 3.2x reduction, so a destination pixel
        /// covers about ten source pixels and bilinear would look at four of them - the rest of
        /// the panel simply would not reach the model. This is what OpenCV calls INTER_AREA. The
        /// cost is one read per source pixel (2.37 MB) against an inference measured in
        /// milliseconds, so it is not worth trading accuracy for.
        ///
        /// **[추정]** If the model was trained on bilinear resizing, this is where accuracy is
        /// quietly lost, and nothing here can detect that. It has to be checked against the model
        /// once one exists for our problem.
        ///
        /// **Takes the frame the acquisition path already read out**, planar gbrp, and crops the
        /// analysis region out of it. It does not read anything from MIL: this repository measured
        /// that a second read of the same frame makes *both* reads four times slower, because they
        /// contend with the acquisition DMA, so `FrameExtractor` reads once and everything that
        /// wants bytes shares that one array under a hold count.
        ///
        /// A region that does not fit the frame is **refused**. Reading it anyway is how a picture
        /// gets assembled out of two different decimations right after somebody changes one.
        /// </summary>
        public byte[] Fill(byte[] planar, int frameWidth, int frameHeight, int roiX, int roiY)
        {
            if (planar == null) throw new ArgumentNullException(nameof(planar));
            if (frameWidth <= 0 || frameHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameWidth));
            if (roiX < 0 || roiY < 0 ||
                roiX + _srcW > frameWidth || roiY + _srcH > frameHeight)
                throw new ArgumentException(
                    $"region {_srcW}x{_srcH} at {roiX},{roiY} does not fit in {frameWidth}x{frameHeight}",
                    nameof(roiX));

            int planeLength = frameWidth * frameHeight;
            if (planar.Length < planeLength * 3)
                throw new ArgumentException(
                    $"frame is {planar.Length} bytes; three {frameWidth}x{frameHeight} planes need "
                  + $"{planeLength * 3}", nameof(planar));

            // gbrp, which is the order FrameExtractor already produces and ffmpeg already takes.
            // Reading it here rather than asking a caller to slice it into three arrays is the
            // whole point: the frame has been read out of MIL exactly once, for every consumer.
            int offG = 0, offB = planeLength, offR = planeLength * 2;

            // The pad is written every call rather than once, because a model that reads it is
            // entitled to a defined value and a reused buffer would otherwise carry the previous
            // frame's picture into the border after a geometry change.
            Array.Clear(_out, 0, _out.Length);

            for (int dy = 0; dy < FittedHeight; dy++)
            {
                int sy0 = _rowStart[dy], sy1 = _rowEnd[dy];
                int rows = sy1 - sy0;
                int outRow = ((dy + PadY) * _outW + PadX) * 3;

                for (int dx = 0; dx < FittedWidth; dx++)
                {
                    int sx0 = _colStart[dx], sx1 = _colEnd[dx];
                    int cols = sx1 - sx0;
                    int n = rows * cols;

                    int sumR = 0, sumG = 0, sumB = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        // Region coordinates into frame coordinates, once per row rather than
                        // per pixel.
                        int row = (roiY + sy) * frameWidth + roiX;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int i = row + sx;
                            sumG += planar[offG + i];
                            sumB += planar[offB + i];
                            sumR += planar[offR + i];
                        }
                    }

                    int o = outRow + dx * 3;
                    _out[o] = (byte)(sumB / n);        // interleaved BGR, which is what the
                    _out[o + 1] = (byte)(sumG / n);    // model graph expects - it does BGR to RGB
                    _out[o + 2] = (byte)(sumR / n);    // internally, and that is easy to "fix" wrongly
                }
            }

            return _out;
        }

        /// <summary>
        /// Turns a model-space box back into frame coordinates.
        ///
        /// **Subtract the padding first, then divide.** The other order is off by `PadY / Scale`,
        /// which on this geometry is 22 px.
        ///
        /// Edges clamp to `width` and `height`, not to `width - 1`: the right edge of a
        /// 1024-wide frame is at 1024, because an edge lies between pixels rather than on one.
        /// </summary>
        public void ToFrame(float mx, float my, float mw, float mh,
                            out float x, out float y, out float w, out float h)
        {
            double x0 = (mx - PadX) / ScaleX;
            double y0 = (my - PadY) / ScaleY;
            double x1 = (mx + mw - PadX) / ScaleX;
            double y1 = (my + mh - PadY) / ScaleY;

            if (x0 < 0.0) x0 = 0.0;
            if (y0 < 0.0) y0 = 0.0;
            if (x1 > _srcW) x1 = _srcW;
            if (y1 > _srcH) y1 = _srcH;
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;

            x = (float)x0;
            y = (float)y0;
            w = (float)(x1 - x0);
            h = (float)(y1 - y0);
        }

        public override string ToString() =>
            $"{_srcW}x{_srcH} -> {_outW}x{_outH}: fit {FittedWidth}x{FittedHeight} "
          + $"at {PadX},{PadY}, sample {ScaleX:F5}/{ScaleY:F5} (nominal {Scale:F5})";
    }
}
