using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// One channel's on-board acquisition ROI, in sensor pixels. Cropping on the camera is the
    /// only lever that reduces host DMA traffic — see research.md section 8: the acquisition
    /// ceiling is a shared ~1.7 GB/s and throttling the display does not move it.
    ///
    /// All four values zero means "no crop" (full sensor). This type holds no MIL dependency so
    /// the snapping arithmetic can be tested without hardware.
    /// </summary>
    public readonly struct ChannelRoi
    {
        public readonly int OffsetX;
        public readonly int OffsetY;
        public readonly int Width;
        public readonly int Height;

        /// <summary>Bayer CFA phase floor: an odd offset or size swaps the colours.</summary>
        private const int CfaIncrement = 2;

        /// <summary>Measured host DMA ceiling shared across channels (research.md section 8).</summary>
        public const double HostDmaCeilingBytesPerSecond = 1.7e9;

        /// <summary>80% of the ceiling — above this the UI warns rather than silently dropping frames.</summary>
        public const double WarnBytesPerSecond = 1.36e9;

        public ChannelRoi(int offsetX, int offsetY, int width, int height)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
        }

        public static ChannelRoi FullFrame => new ChannelRoi(0, 0, 0, 0);

        /// <summary>
        /// True when this means "use the whole sensor". A non-positive size counts as full frame
        /// rather than an error, so a half-filled settings file degrades to the safe default.
        /// </summary>
        public bool IsFullFrame => Width <= 0 || Height <= 0;

        /// <summary>
        /// Rounds this ROI onto the hardware's increment grid and clamps it inside the sensor.
        ///
        /// Offsets and sizes both round DOWN: rounding a size up could push the ROI past the
        /// sensor edge, and rounding an offset up would move the crop off the region the operator
        /// picked. The increments come from M_FEATURE_INCREMENT and are widened by EvenIncrement so
        /// the result is always even — see that method for why flooring at 2 was not enough.
        /// </summary>
        public ChannelRoi Snap(int xInc, int yInc, int wInc, int hInc, int maxWidth, int maxHeight)
        {
            if (IsFullFrame)
                return FullFrame;

            int ex = EvenIncrement(xInc);
            int ey = EvenIncrement(yInc);
            int ew = EvenIncrement(wInc);
            int eh = EvenIncrement(hInc);

            // Offset first: it bounds how much width is left. Leave at least one width increment.
            int x = RoundDown(Clamp(OffsetX, 0, Math.Max(0, maxWidth - ew)), ex);
            int y = RoundDown(Clamp(OffsetY, 0, Math.Max(0, maxHeight - eh)), ey);

            int w = RoundDown(Clamp(Width, ew, maxWidth - x), ew);
            int h = RoundDown(Clamp(Height, eh, maxHeight - y), eh);

            // Clamp above can leave a rounded-down size of zero when very little room remains.
            // MIL rejects a zero-size ROI, so give back one increment and pull the offset in.
            if (w < ew) { w = ew; x = RoundDown(Math.Max(0, maxWidth - ew), ex); }
            if (h < eh) { h = eh; y = RoundDown(Math.Max(0, maxHeight - eh), ey); }

            return new ChannelRoi(x, y, w, h);
        }

        /// <summary>
        /// Bytes one frame occupies for this ROI. <paramref name="bands"/> is 3 with on-board Bayer
        /// conversion enabled and 1 without — the 3x difference is what makes cropping necessary.
        /// </summary>
        public long BytesPerFrame(int bands, int maxWidth, int maxHeight)
        {
            long w = IsFullFrame ? maxWidth : Width;
            long h = IsFullFrame ? maxHeight : Height;
            return w * h * Math.Max(1, bands);
        }

        public override string ToString() =>
            IsFullFrame ? "full frame" : $"{Width}x{Height} @ {OffsetX},{OffsetY}";

        /// <summary>
        /// The smallest step that satisfies both the hardware's increment and the even-pixel rule.
        ///
        /// Flooring at 2 is not enough: a camera reporting an increment of 3 would let offset 9
        /// and width 9 straight through, shifting the CFA phase and swapping the colours. Nor can
        /// an odd increment simply be rounded up to 4 — 4 is not a multiple of 3, so the hardware
        /// would reject or re-snap it. Doubling an odd increment gives the least common multiple
        /// of it and 2, which satisfies both.
        /// </summary>
        private static int EvenIncrement(int increment)
        {
            int i = Math.Max(CfaIncrement, increment);
            if (i % 2 == 0)
                return i;
            // Doubling an absurd increment would overflow to a negative step, and a negative step
            // sends RoundDown off-grid instead of rejecting the value. Nothing near this is a real
            // camera increment, so fall back to the CFA minimum.
            return i > int.MaxValue / 2 ? CfaIncrement : i * 2;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static int RoundDown(int v, int increment) => v - (v % increment);
    }
}
