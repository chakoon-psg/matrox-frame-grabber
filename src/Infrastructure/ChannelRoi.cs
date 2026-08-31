using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// One channel's ANALYSIS region — which pixels the anomaly metrics are computed over, in
    /// coordinates of the (possibly decimated) acquired frame. All four values zero means the
    /// whole frame.
    ///
    /// This used to be the camera's acquisition ROI. It is not: this camera accepts writes to
    /// Width/Height/OffsetX/OffsetY and ignores them (see CLAUDE.md), so payload reduction is done
    /// with DecimationHorizontal/Vertical instead. As a software rectangle there is no hardware
    /// increment to satisfy and nothing can refuse it — only even alignment still earns its keep,
    /// so every tile of the metric grid sees the same Bayer CFA phase.
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
        /// Rounds this region onto an even-pixel grid and clamps it inside the frame.
        ///
        /// Offsets and sizes both round DOWN: rounding a size up could push the region past the
        /// frame edge, and rounding an offset up would move it off the area the operator picked.
        /// Even alignment keeps the Bayer CFA phase identical in every metric tile.
        /// </summary>
        public ChannelRoi Snap(int maxWidth, int maxHeight)
        {
            if (IsFullFrame)
                return FullFrame;

            const int ex = CfaIncrement, ey = CfaIncrement, ew = CfaIncrement, eh = CfaIncrement;

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

        /// <summary>Decimation factors this app offers. Powers of two keep the CFA phase intact.</summary>
        public static readonly int[] AllowedDecimation = { 1, 2, 4 };

        /// <summary>
        /// Snaps a requested decimation factor to one this app offers. Anything unrecognised
        /// becomes 1 (full resolution) rather than an error: a bad settings value must degrade to
        /// the safe default, not refuse to start.
        /// </summary>
        public static int ClampDecimation(int requested)
        {
            foreach (int allowed in AllowedDecimation)
                if (allowed == requested)
                    return requested;
            return 1;
        }

        /// <summary>Frame width the camera delivers at this decimation factor.</summary>
        public static int DecimatedWidth(int sensorWidth, int decimation) =>
            sensorWidth / ClampDecimation(decimation);

        /// <summary>Frame height the camera delivers at this decimation factor.</summary>
        public static int DecimatedHeight(int sensorHeight, int decimation) =>
            sensorHeight / ClampDecimation(decimation);

        public override string ToString() =>
            IsFullFrame ? "full frame" : $"{Width}x{Height} @ {OffsetX},{OffsetY}";

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static int RoundDown(int v, int increment) => v - (v % increment);
    }
}
