using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Maps image pixels to positions inside the display control, so an ROI rectangle can be drawn
    /// over the live view in WPF.
    ///
    /// MIL's own overlay plane is not composited by the WPF display control, so the rectangle has
    /// to be drawn by us — which means reproducing where MIL put the image inside the control.
    /// Measured behaviour (2026-08-20): when the scaled image is smaller than the control MIL
    /// centres it, and M_REAL_OFFSET_X reads 0 in that state even though the image is visibly
    /// inset. So centring is applied here whenever the image fits, and MIL's offsets are used only
    /// once the image is larger than the control, which is the only case where they describe a
    /// scroll position rather than nothing.
    /// </summary>
    public readonly struct DisplayMapping
    {
        /// <summary>Control-space position of image pixel (0,0).</summary>
        public readonly double OriginX;
        public readonly double OriginY;
        /// <summary>Control pixels per image pixel.</summary>
        public readonly double Scale;

        public DisplayMapping(double originX, double originY, double scale)
        {
            OriginX = originX;
            OriginY = originY;
            Scale = scale;
        }

        /// <summary>True when the mapping is usable (a positive scale).</summary>
        public bool IsValid => Scale > 0;

        /// <summary>
        /// Builds the mapping. <paramref name="zoom"/> is MIL's M_REAL_ZOOM_FACTOR_X and
        /// <paramref name="offsetX"/>/<paramref name="offsetY"/> are M_REAL_OFFSET_X/Y, in image
        /// pixels. Returns an invalid mapping for degenerate inputs rather than throwing, because
        /// the caller is a render path that must not fail.
        /// </summary>
        public static DisplayMapping Create(
            double controlWidth, double controlHeight,
            int frameWidth, int frameHeight,
            double zoom, double offsetX, double offsetY)
        {
            if (controlWidth <= 0 || controlHeight <= 0 || frameWidth <= 0 || frameHeight <= 0 || zoom <= 0)
                return new DisplayMapping(0, 0, 0);

            double shownWidth = frameWidth * zoom;
            double shownHeight = frameHeight * zoom;

            double originX = shownWidth <= controlWidth
                ? (controlWidth - shownWidth) / 2.0     // MIL centres an image that fits
                : -offsetX * zoom;                      // scrolled: the offset is the scroll position
            double originY = shownHeight <= controlHeight
                ? (controlHeight - shownHeight) / 2.0
                : -offsetY * zoom;

            return new DisplayMapping(originX, originY, zoom);
        }

        public double ToControlX(double imageX) => OriginX + imageX * Scale;
        public double ToControlY(double imageY) => OriginY + imageY * Scale;

        /// <summary>Inverse, for turning a mouse position back into an image pixel.</summary>
        public double ToImageX(double controlX) => Scale > 0 ? (controlX - OriginX) / Scale : 0;
        public double ToImageY(double controlY) => Scale > 0 ? (controlY - OriginY) / Scale : 0;
    }
}
