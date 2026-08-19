using System;
using System.Diagnostics;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Measures one channel's brightness off the acquisition path: the stats tick calls
    /// <see cref="Sample"/>, which reads a scatter of strips out of the display buffer.
    ///
    /// It deliberately point-samples at full resolution rather than measuring a downscaled copy.
    /// Bilinear downscaling averages a clipped pixel together with its neighbours — 255 beside 200
    /// becomes 227 — which erases exactly the failure the clipping figures exist to catch.
    ///
    /// The strips are spread down the frame rather than taken as one contiguous band, so a light
    /// source in the top of the frame cannot hide from the sample.
    /// </summary>
    public sealed class BrightnessMeter
    {
        /// <summary>Strips spread evenly down the frame.</summary>
        private const int Strips = 16;
        /// <summary>Rows read per strip. 16 x 4 = 64 rows of a 1544-row frame, about 4%.</summary>
        private const int RowsPerStrip = 4;

        private byte[] _r, _g, _b;
        private int _stripBytes;

        public BrightnessHistory History { get; } = new BrightnessHistory();

        /// <summary>Wall time the last <see cref="Sample"/> took, for the 500 ms budget check.</summary>
        public double LastSampleMs { get; private set; }

        /// <summary>Consecutive Sample calls that ended in a MIL failure. Zero after any success.</summary>
        public int ConsecutiveFailures { get; private set; }

        /// <summary>
        /// Reads one brightness reading from <paramref name="displayBuffer"/> and appends it.
        /// Does nothing when the buffer is unbound. Never throws: a MIL failure here must not cost
        /// the caller its stats tick.
        /// </summary>
        public void Sample(MIL_ID displayBuffer)
        {
            if (displayBuffer == MIL.M_NULL)
                return;

            var watch = Stopwatch.StartNew();
            try
            {
                MIL_INT width = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_X, MIL.M_NULL);
                MIL_INT height = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_Y, MIL.M_NULL);
                MIL_INT bands = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_BAND, MIL.M_NULL);
                if (width <= 0 || height < RowsPerStrip)
                    return;

                // The display buffer's bit depth follows the camera (CameraChannel applies
                // M_BIT_SHIFT for anything over 8 bits). Sampling raw bytes from a >8-bit buffer
                // would read the high byte only, pinning luma near 255 and clipping near 100% —
                // a permanent false-saturation warning. A blank graph is more honest than that.
                MIL_INT sizeBit = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_BIT, MIL.M_NULL);
                if (sizeBit > 8)
                    return;   // deeper buffers would need a ushort path; a blank graph beats a fabricated one

                EnsureBuffers((int)width);

                if (bands >= 3)
                    SampleColor(displayBuffer, (int)width, (int)height);
                else
                    SampleMono(displayBuffer, (int)width, (int)height);
            }
            catch (MILException)
            {
                // A buffer can be freed and reallocated underneath us when a camera is reloaded —
                // losing one reading is not worth disturbing the caller's tick. But if this keeps
                // happening, something is permanently wrong (e.g. MbufChildColor rejecting a
                // buffer layout), and a silently-blank strip would be the worst failure mode for a
                // feature whose entire output is numbers — so count it instead of just swallowing it.
                ConsecutiveFailures++;
            }
            finally
            {
                LastSampleMs = watch.Elapsed.TotalMilliseconds;
            }
        }

        public void Reset() => History.Clear();

        private void EnsureBuffers(int width)
        {
            int needed = width * RowsPerStrip;
            if (_stripBytes == needed && _r != null)
                return;
            _stripBytes = needed;
            _r = new byte[needed];
            _g = new byte[needed];
            _b = new byte[needed];
        }

        private void SampleColor(MIL_ID buffer, int width, int height)
        {
            // Band children are created and freed inside this call rather than cached. Caching them
            // would tie their lifetime to the display buffer's, and a child outliving its parent is
            // the failure this codebase already documents.
            MIL_ID red = MIL.M_NULL, green = MIL.M_NULL, blue = MIL.M_NULL;
            try
            {
                // Numeric band indices, matching the convention RecordingSession.cs uses for the
                // same MbufChildColor call: per MIL's own MbufChildColor reference, for an RGB
                // parent buffer index 0/1/2 is defined to be red/green/blue, exactly what
                // M_RED/M_GREEN/M_BLUE resolve to — the two spellings are documented as equivalent,
                // not two different conventions. Matched here for consistency with RecordingSession.
                MIL.MbufChildColor(buffer, 0, ref red);     // band 0 (R)
                MIL.MbufChildColor(buffer, 1, ref green);   // band 1 (G)
                MIL.MbufChildColor(buffer, 2, ref blue);    // band 2 (B)

                double lumaSum = 0;
                long clipped = 0, black = 0, counted = 0;
                int step = height / Strips;

                for (int s = 0; s < Strips; s++)
                {
                    int y = s * step;
                    if (y + RowsPerStrip > height)
                        break;

                    MIL.MbufGet2d(red, 0, y, width, RowsPerStrip, _r);
                    MIL.MbufGet2d(green, 0, y, width, RowsPerStrip, _g);
                    MIL.MbufGet2d(blue, 0, y, width, RowsPerStrip, _b);

                    for (int i = 0; i < _stripBytes; i++)
                    {
                        byte r = _r[i], g = _g[i], b = _b[i];
                        lumaSum += 0.299 * r + 0.587 * g + 0.114 * b;
                        if (r == 255 || g == 255 || b == 255) clipped++;
                        if (r == 0 && g == 0 && b == 0) black++;
                        counted++;
                    }
                }

                Append(lumaSum, clipped, black, counted);
            }
            finally
            {
                if (blue != MIL.M_NULL) MIL.MbufFree(blue);
                if (green != MIL.M_NULL) MIL.MbufFree(green);
                if (red != MIL.M_NULL) MIL.MbufFree(red);
            }
        }

        private void SampleMono(MIL_ID buffer, int width, int height)
        {
            // RAW recording turns M_BAYER_CONVERSION off, so the display buffer becomes the
            // single-band Bayer mosaic. Its plain mean is 0.25R + 0.50G + 0.25B by the CFA's own
            // 1R:2G:1B ratio, which is close enough to Rec.601 that the graph stays continuous
            // when a RAW recording starts.
            double lumaSum = 0;
            long clipped = 0, black = 0, counted = 0;
            int step = height / Strips;

            for (int s = 0; s < Strips; s++)
            {
                int y = s * step;
                if (y + RowsPerStrip > height)
                    break;

                MIL.MbufGet2d(buffer, 0, y, width, RowsPerStrip, _r);

                for (int i = 0; i < _stripBytes; i++)
                {
                    byte v = _r[i];
                    lumaSum += v;
                    if (v == 255) clipped++;
                    if (v == 0) black++;
                    counted++;
                }
            }

            Append(lumaSum, clipped, black, counted);
        }

        private void Append(double lumaSum, long clipped, long black, long counted)
        {
            if (counted == 0)
                return;
            History.Add(new BrightnessSample(
                (float)(lumaSum / counted),
                (float)(100.0 * clipped / counted),
                (float)(100.0 * black / counted)));
            ConsecutiveFailures = 0;
        }
    }
}
