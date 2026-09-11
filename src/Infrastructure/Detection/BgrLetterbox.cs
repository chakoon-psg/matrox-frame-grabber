using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Turns an acquired planar frame into the small interleaved picture a model wants, and
    /// remembers how to bring coordinates back.
    ///
    /// **This class is why no image library enters the build.** The one it replaces used OpenCV
    /// for exactly three things - a bilinear resize, a paste into a padded canvas, and a copy out
    /// to a byte array - and that cost 93.7 MB of native payload on win-x64, 27.3 MB of which was
    /// a second copy of ffmpeg in an app that already finds ffmpeg.exe at runtime. The same
    /// analysis measured the whole of preprocessing and decoding at under 2% of inference, below
    /// run-to-run noise, so there was nothing to lose by writing the three things out.
    ///
    /// Two details are ours rather than inherited:
    ///
    /// **Planar in, interleaved out, in one pass.** Frames arrive as three separate planes in
    /// G, B, R order - that is what `gbrp` means and it is what the grab path already produces -
    /// while a model wants B, G, R next to each other per pixel. De-planarising and scaling are
    /// the same walk over the source, so they are one walk.
    ///
    /// **Box average, not bilinear.** 1024 to 320 is a 3.2x reduction and bilinear samples four
    /// source pixels out of the ten a destination pixel covers, which aliases: a one-pixel line
    /// on a test pattern can vanish or double. Averaging the whole covered rectangle is what
    /// INTER_AREA does and it costs one read per source pixel, 2.37 MB against a 2.77 ms
    /// inference. If a model turns out to have been trained on bilinear input this is the knob to
    /// revisit - it is a known way to lose accuracy quietly.
    ///
    /// **Not thread safe.** <see cref="Picture"/> is reused on every fill, which is the point.
    /// </summary>
    public sealed class BgrLetterbox
    {
        private readonly byte[] _picture;

        public BgrLetterbox(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
        {
            if (sourceWidth < 1) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
            if (sourceHeight < 1) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
            if (outputWidth < 1) throw new ArgumentOutOfRangeException(nameof(outputWidth));
            if (outputHeight < 1) throw new ArgumentOutOfRangeException(nameof(outputHeight));

            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;

            // The smaller ratio, so the whole frame fits and the aspect is kept. The remainder is
            // the padding, split evenly, which is what "letterbox" means.
            Scale = Math.Min(outputWidth / (double)sourceWidth,
                             outputHeight / (double)sourceHeight);
            ScaledWidth = Math.Max(1, Math.Min(outputWidth, (int)Math.Round(sourceWidth * Scale)));
            ScaledHeight = Math.Max(1, Math.Min(outputHeight, (int)Math.Round(sourceHeight * Scale)));
            PadX = (outputWidth - ScaledWidth) / 2;
            PadY = (outputHeight - ScaledHeight) / 2;
            ScaleX = ScaledWidth / (double)sourceWidth;
            ScaleY = ScaledHeight / (double)sourceHeight;

            _picture = new byte[(long)outputWidth * outputHeight * 3 > int.MaxValue
                                ? throw new ArgumentOutOfRangeException(nameof(outputWidth))
                                : outputWidth * outputHeight * 3];
        }

        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int OutputWidth { get; }
        public int OutputHeight { get; }

        /// <summary>
        /// The ratio the reduced size was chosen from. **Not what maps a coordinate back** - see
        /// <see cref="ScaleX"/>.
        /// </summary>
        public double Scale { get; }

        /// <summary>
        /// What the fill actually samples at, per axis.
        ///
        /// These are not <see cref="Scale"/>. The reduced size is a whole number of pixels, so
        /// 772 x 0.3125 = 241.25 becomes 241 and the real vertical ratio is 241/772 = 0.31218.
        /// Inverting with the nominal scale puts the bottom edge of the frame at 771.2 instead of
        /// 772 and every coordinate up to 1.2 px out - small, silent, and exactly the kind of
        /// error a coordinate-producing feature must not have. A test caught this one.
        /// </summary>
        public double ScaleX { get; }

        public double ScaleY { get; }

        /// <summary>Size the frame is reduced to, before padding.</summary>
        public int ScaledWidth { get; }
        public int ScaledHeight { get; }

        /// <summary>Where the reduced frame sits inside the output.</summary>
        public int PadX { get; }
        public int PadY { get; }

        /// <summary>Bytes one source frame must have: width x height x 3 planes.</summary>
        public int SourceBytes => SourceWidth * SourceHeight * 3;

        /// <summary>
        /// The picture, interleaved BGR, reused between calls. Valid until the next
        /// <see cref="FillFromPlanar"/>.
        /// </summary>
        public byte[] Picture => _picture;

        /// <summary>
        /// Fills <see cref="Picture"/> from a planar G, B, R frame.
        ///
        /// False when the frame is the wrong size, rather than reading past it - a caller that
        /// changed decimation mid-run would otherwise hand over a short buffer and get a picture
        /// made partly of the previous frame.
        /// </summary>
        public bool FillFromPlanar(byte[] planarGbr)
        {
            if (planarGbr == null || planarGbr.Length < SourceBytes) return false;

            int plane = SourceWidth * SourceHeight;
            int gBase = 0, bBase = plane, rBase = plane * 2;

            Array.Clear(_picture, 0, _picture.Length);   // the padding, once per fill

            for (int dy = 0; dy < ScaledHeight; dy++)
            {
                int sy0 = (int)((long)dy * SourceHeight / ScaledHeight);
                int sy1 = (int)((long)(dy + 1) * SourceHeight / ScaledHeight);
                if (sy1 <= sy0) sy1 = sy0 + 1;
                if (sy1 > SourceHeight) sy1 = SourceHeight;

                int outRow = ((PadY + dy) * OutputWidth + PadX) * 3;

                for (int dx = 0; dx < ScaledWidth; dx++)
                {
                    int sx0 = (int)((long)dx * SourceWidth / ScaledWidth);
                    int sx1 = (int)((long)(dx + 1) * SourceWidth / ScaledWidth);
                    if (sx1 <= sx0) sx1 = sx0 + 1;
                    if (sx1 > SourceWidth) sx1 = SourceWidth;

                    int sumG = 0, sumB = 0, sumR = 0, n = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int row = sy * SourceWidth;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int i = row + sx;
                            sumG += planarGbr[gBase + i];
                            sumB += planarGbr[bBase + i];
                            sumR += planarGbr[rBase + i];
                            n++;
                        }
                    }

                    int o = outRow + dx * 3;
                    _picture[o] = (byte)(sumB / n);       // B
                    _picture[o + 1] = (byte)(sumG / n);   // G
                    _picture[o + 2] = (byte)(sumR / n);   // R
                }
            }

            return true;
        }

        /// <summary>
        /// Brings one coordinate of the small picture back to the acquired frame.
        ///
        /// Undoing the padding before the scale, in that order - the other order is the classic
        /// way to be off by PadY/Scale, which at this geometry is 22 px. And dividing by
        /// <see cref="ScaleY"/> rather than <see cref="Scale"/>, which is the smaller error of
        /// the same kind.
        /// </summary>
        public double ToSourceX(double pictureX) => (pictureX - PadX) / ScaleX;

        public double ToSourceY(double pictureY) => (pictureY - PadY) / ScaleY;

        /// <summary>A length has no origin, so only the ratio applies - but it still has an axis.</summary>
        public double ToSourceWidth(double pictureWidth) => pictureWidth / ScaleX;

        public double ToSourceHeight(double pictureHeight) => pictureHeight / ScaleY;

        /// <summary>
        /// Maps a whole finding back and clamps it to the frame.
        ///
        /// Clamped to width and height, not width-1: an edge at 1024 on a 1024-wide frame is the
        /// right edge, and subtracting one there is a pixel of error that only shows up when
        /// somebody measures against it.
        /// </summary>
        public ScreenFinding ToSource(ScreenFinding picture)
        {
            double x = ToSourceX(picture.X);
            double y = ToSourceY(picture.Y);
            double right = ToSourceX(picture.Right);
            double bottom = ToSourceY(picture.Bottom);

            if (x < 0.0) x = 0.0;
            if (y < 0.0) y = 0.0;
            if (right > SourceWidth) right = SourceWidth;
            if (bottom > SourceHeight) bottom = SourceHeight;
            if (right < x) right = x;
            if (bottom < y) bottom = y;

            return new ScreenFinding(picture.Kind, picture.Score,
                                     (float)x, (float)y,
                                     (float)(right - x), (float)(bottom - y));
        }

        /// <summary>A line for the log, so a run says what geometry its findings came through.</summary>
        public string Describe() =>
            $"{SourceWidth}x{SourceHeight} -> {ScaledWidth}x{ScaledHeight} "
          + $"in {OutputWidth}x{OutputHeight} (scale {ScaleX:F5} x {ScaleY:F5}, pad {PadX},{PadY})";
    }
}
