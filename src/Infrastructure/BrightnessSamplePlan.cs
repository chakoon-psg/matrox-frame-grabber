using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The rectangle a brightness reading is taken from, and how the strips are laid out inside it.
    /// All values are in pixels of the acquired (already decimated) frame — the space
    /// <see cref="ChannelRoi"/> is stored in and the display buffer is allocated in.
    /// </summary>
    public readonly struct BrightnessSampleRegion
    {
        public readonly int OffsetX;
        public readonly int OffsetY;
        public readonly int Width;
        public readonly int Height;

        /// <summary>Strips actually taken — fewer than the nominal count in a short region.</summary>
        public readonly int Strips;

        /// <summary>Rows between the tops of consecutive strips. Zero when there is only one.</summary>
        public readonly int Step;

        public BrightnessSampleRegion(int offsetX, int offsetY, int width, int height, int strips, int step)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
            Strips = strips;
            Step = step;
        }

        /// <summary>Nothing measurable — the caller appends no reading rather than a fabricated one.</summary>
        public bool IsEmpty => Width <= 0 || Height <= 0 || Strips <= 0;

        /// <summary>Top row of strip <paramref name="index"/>, in frame coordinates.</summary>
        public int StripTop(int index) => OffsetY + index * Step;

        public static BrightnessSampleRegion Empty => new BrightnessSampleRegion(0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Turns an analysis ROI into the strips a brightness reading is sampled from.
    ///
    /// Split out of <c>BrightnessMeter</c> because it is the part with the edge cases — a rectangle
    /// shorter than one strip, a rectangle left over from another decimation factor, no rectangle
    /// at all — and <c>BrightnessMeter</c> needs MIL, so it cannot be tested on a machine without a
    /// board. This can.
    /// </summary>
    public static class BrightnessSamplePlan
    {
        /// <summary>Strips spread evenly down the region.</summary>
        public const int Strips = 16;

        /// <summary>Rows read per strip. 16 x 4 = 64 rows of a 1544-row frame, about 4%.</summary>
        public const int RowsPerStrip = 4;

        /// <summary>
        /// Where to sample for <paramref name="roi"/> in a <paramref name="frameWidth"/> x
        /// <paramref name="frameHeight"/> frame.
        ///
        /// A full-frame ROI means "no region was chosen", and measures the whole frame — that is
        /// what an operator who has not drawn a rectangle expects to see.
        ///
        /// A rectangle that was chosen but cannot be sampled returns <see cref="BrightnessSampleRegion.Empty"/>
        /// rather than falling back to the whole frame. Falling back would keep the graph moving
        /// while silently measuring something the operator did not ask for, which is the one
        /// outcome they cannot see; a gap in the graph is visible.
        /// </summary>
        public static BrightnessSampleRegion For(ChannelRoi roi, int frameWidth, int frameHeight)
        {
            if (frameWidth <= 0 || frameHeight <= 0)
                return BrightnessSampleRegion.Empty;

            int x = 0, y = 0, w = frameWidth, h = frameHeight;

            if (!roi.IsFullFrame)
            {
                // Clamp rather than trust. The stored rectangle can outlive the frame it was drawn
                // in — a settings file written at another decimation factor, or edited by hand —
                // and reading outside the buffer is a MIL error, not a wrong number.
                x = Clamp(roi.OffsetX, 0, frameWidth);
                y = Clamp(roi.OffsetY, 0, frameHeight);
                w = Math.Min(roi.Width, frameWidth - x);
                h = Math.Min(roi.Height, frameHeight - y);

                if (w <= 0 || h <= 0)
                    return BrightnessSampleRegion.Empty;
            }

            // Non-overlapping strips: sampling the same rows twice would not change the mean, but it
            // would report a `counted` that overstates how much of the region was actually looked at.
            int strips = Math.Min(Strips, h / RowsPerStrip);
            if (strips <= 0)
                return BrightnessSampleRegion.Empty;

            // Spread so the last strip still ends on the region's last row. Spacing by h/strips
            // instead would leave the bottom of the region never sampled — for a full 1544-row frame
            // that is the bottom 100 rows, exactly the corner a stray light source hides in.
            int step = strips > 1 ? (h - RowsPerStrip) / (strips - 1) : 0;

            return new BrightnessSampleRegion(x, y, w, h, strips, step);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
