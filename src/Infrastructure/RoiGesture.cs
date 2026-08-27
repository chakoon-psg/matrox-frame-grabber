using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>What a press landed on: a resize handle, the rectangle's body, or nothing.</summary>
    public enum RoiHandle
    {
        None,
        TopLeft, Top, TopRight,
        Left, Right,
        BottomLeft, Bottom, BottomRight,
        Inside
    }

    /// <summary>
    /// Every rule that decides where a dragged analysis rectangle ends up.
    ///
    /// This is deliberately free of mouse events, controls and MIL: two surfaces edit the same
    /// rectangle — the pane and the fullscreen overlay — and rules duplicated across two sets of
    /// handlers drift. Keeping them here also means they can be checked without a board, which is
    /// the only kind of test this project can run.
    ///
    /// Everything here works in image pixels of the acquired (already decimated) frame, the same
    /// coordinates <see cref="ChannelRoi"/> is stored in. Converting from the control's pixels is
    /// the caller's job, through <see cref="DisplayMapping"/>.
    /// </summary>
    public static class RoiGesture
    {
        /// <summary>Smallest rectangle a drag can produce — see <see cref="ChannelRoi.MinEditableSize"/>.</summary>
        public const int MinSize = ChannelRoi.MinEditableSize;

        /// <summary>
        /// The handle under a point, or <see cref="RoiHandle.Inside"/> / <see cref="RoiHandle.None"/>.
        /// <paramref name="tolerance"/> is in image pixels — the caller converts its control-pixel
        /// grab radius through the display scale, so the target stays the same size on screen
        /// however far the operator has zoomed in.
        /// </summary>
        public static RoiHandle HitTest(ChannelRoi roi, double x, double y, double tolerance)
        {
            if (roi.IsFullFrame)
                return RoiHandle.None;

            double left = roi.OffsetX, right = roi.OffsetX + roi.Width;
            double top = roi.OffsetY, bottom = roi.OffsetY + roi.Height;

            bool nearLeft = Math.Abs(x - left) <= tolerance;
            bool nearRight = Math.Abs(x - right) <= tolerance;
            bool nearTop = Math.Abs(y - top) <= tolerance;
            bool nearBottom = Math.Abs(y - bottom) <= tolerance;
            bool withinX = x >= left - tolerance && x <= right + tolerance;
            bool withinY = y >= top - tolerance && y <= bottom + tolerance;

            // Corners before edges. A corner is the smaller target and the only one that moves both
            // axes, so where both match the corner is what the operator was aiming at.
            if (nearLeft && nearTop) return RoiHandle.TopLeft;
            if (nearRight && nearTop) return RoiHandle.TopRight;
            if (nearLeft && nearBottom) return RoiHandle.BottomLeft;
            if (nearRight && nearBottom) return RoiHandle.BottomRight;

            if (nearLeft && withinY) return RoiHandle.Left;
            if (nearRight && withinY) return RoiHandle.Right;
            if (nearTop && withinX) return RoiHandle.Top;
            if (nearBottom && withinX) return RoiHandle.Bottom;

            if (x > left && x < right && y > top && y < bottom) return RoiHandle.Inside;
            return RoiHandle.None;
        }

        /// <summary>True for the handles that resize rather than move.</summary>
        public static bool IsResize(RoiHandle handle) =>
            handle != RoiHandle.None && handle != RoiHandle.Inside;

        /// <summary>
        /// The rectangle after dragging <paramref name="handle"/> to (<paramref name="x"/>,
        /// <paramref name="y"/>). The side opposite the handle stays where it is; an edge handle
        /// leaves the other axis untouched.
        ///
        /// Dragging past the pinned side flips the rectangle rather than inverting it. That is not
        /// cosmetic: <see cref="ChannelRoi"/> reads a non-positive size as "the whole frame", so an
        /// inverted rectangle would silently switch the channel to measuring everything.
        /// </summary>
        public static ChannelRoi Resize(ChannelRoi roi, RoiHandle handle, double x, double y,
                                        int frameWidth, int frameHeight)
        {
            if (roi.IsFullFrame || !IsResize(handle))
                return roi;

            int left = roi.OffsetX, right = roi.OffsetX + roi.Width;
            int top = roi.OffsetY, bottom = roi.OffsetY + roi.Height;

            bool movesLeft = handle == RoiHandle.TopLeft || handle == RoiHandle.Left || handle == RoiHandle.BottomLeft;
            bool movesRight = handle == RoiHandle.TopRight || handle == RoiHandle.Right || handle == RoiHandle.BottomRight;
            bool movesTop = handle == RoiHandle.TopLeft || handle == RoiHandle.Top || handle == RoiHandle.TopRight;
            bool movesBottom = handle == RoiHandle.BottomLeft || handle == RoiHandle.Bottom || handle == RoiHandle.BottomRight;

            if (movesLeft || movesRight)
            {
                int pinned = movesLeft ? right : left;
                Span1D(pinned, (int)Math.Round(x), frameWidth, out left, out right);
            }
            if (movesTop || movesBottom)
            {
                int pinned = movesTop ? bottom : top;
                Span1D(pinned, (int)Math.Round(y), frameHeight, out top, out bottom);
            }

            // Snap last, and through ChannelRoi's own rule, so the preview shows exactly what the
            // commit will store. Anything else makes the rectangle jump when the button comes up.
            return new ChannelRoi(left, top, right - left, bottom - top)
                       .Snap(frameWidth, frameHeight);
        }

        /// <summary>
        /// One axis of a resize: the pinned coordinate stays, the dragged one lands where the
        /// pointer is, and the result is ordered, at least <see cref="MinSize"/> long, and inside
        /// the frame.
        /// </summary>
        private static void Span1D(int pinned, int dragged, int frameMax, out int lo, out int hi)
        {
            dragged = Clamp(dragged, 0, frameMax);
            lo = Math.Min(pinned, dragged);
            hi = Math.Max(pinned, dragged);

            if (hi - lo >= MinSize)
                return;

            // Too short: grow away from the pinned side, which is the one the operator is not
            // holding. Only if that runs into the wall does the pinned side have to give way.
            if (dragged >= pinned)
                hi = lo + MinSize;
            else
                lo = hi - MinSize;

            if (hi > frameMax) { hi = frameMax; lo = hi - MinSize; }
            if (lo < 0) { lo = 0; hi = MinSize; }
        }

        /// <summary>
        /// The rectangle shifted by (<paramref name="dx"/>, <paramref name="dy"/>), stopped at the
        /// frame edges. Moving keeps the size — a rectangle that shrank as it reached the edge
        /// would quietly change what is being measured.
        /// </summary>
        public static ChannelRoi Move(ChannelRoi roi, int dx, int dy, int frameWidth, int frameHeight)
        {
            if (roi.IsFullFrame)
                return roi;

            int x = Clamp(roi.OffsetX + dx, 0, Math.Max(0, frameWidth - roi.Width));
            int y = Clamp(roi.OffsetY + dy, 0, Math.Max(0, frameHeight - roi.Height));

            return new ChannelRoi(x, y, roi.Width, roi.Height).Snap(frameWidth, frameHeight);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
