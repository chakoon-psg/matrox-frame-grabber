using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The analysis rectangle is edited by dragging it, and every rule that decides where it ends
    /// up lives here rather than in the mouse handlers — so the pane and the fullscreen overlay
    /// cannot disagree, and so the rules can be checked without a board.
    /// </summary>
    public class RoiGestureTests
    {
        // The real decimation-2 frame this camera delivers, and a rectangle inside it.
        const int FrameW = 1024;
        const int FrameH = 772;

        static ChannelRoi Roi() => new ChannelRoi(400, 300, 200, 100);   // right 600, bottom 400

        // ----- Finding the handle under the pointer -----

        [Theory]
        [InlineData(400, 300, RoiHandle.TopLeft)]
        [InlineData(500, 300, RoiHandle.Top)]
        [InlineData(600, 300, RoiHandle.TopRight)]
        [InlineData(400, 350, RoiHandle.Left)]
        [InlineData(600, 350, RoiHandle.Right)]
        [InlineData(400, 400, RoiHandle.BottomLeft)]
        [InlineData(500, 400, RoiHandle.Bottom)]
        [InlineData(600, 400, RoiHandle.BottomRight)]
        public void HitTest_FindsEachHandleAtItsOwnPosition(double x, double y, RoiHandle expected)
        {
            Assert.Equal(expected, RoiGesture.HitTest(Roi(), x, y, tolerance: 6));
        }

        [Fact]
        public void HitTest_ReturnsInsideForTheMiddle()
        {
            Assert.Equal(RoiHandle.Inside, RoiGesture.HitTest(Roi(), 500, 350, tolerance: 6));
        }

        [Fact]
        public void HitTest_ReturnsNoneWellOutside()
        {
            Assert.Equal(RoiHandle.None, RoiGesture.HitTest(Roi(), 50, 50, tolerance: 6));
        }

        [Fact]
        public void HitTest_PrefersTheCornerWhereACornerAndAnEdgeBothMatch()
        {
            // Two pixels in from the top-left corner is within tolerance of the top edge, the left
            // edge and the corner. The corner is the useful answer: it is the only one that moves
            // both axes, and it is the smaller target.
            Assert.Equal(RoiHandle.TopLeft, RoiGesture.HitTest(Roi(), 402, 302, tolerance: 6));
        }

        // ----- Resizing: the opposite side stays put -----

        [Fact]
        public void Resize_Right_LeavesTheLeftEdgeWhereItWas()
        {
            var r = RoiGesture.Resize(Roi(), RoiHandle.Right, 700, 350, FrameW, FrameH);

            Assert.Equal(400, r.OffsetX);
            Assert.Equal(300, r.Width);          // 700 - 400
        }

        [Fact]
        public void Resize_Top_LeavesTheBottomEdgeWhereItWas()
        {
            var r = RoiGesture.Resize(Roi(), RoiHandle.Top, 500, 200, FrameW, FrameH);

            Assert.Equal(200, r.OffsetY);
            Assert.Equal(200, r.Height);         // bottom stays at 400
        }

        [Fact]
        public void Resize_EdgeHandle_ChangesOneAxisOnly()
        {
            // Dragging the left edge sideways must not move the top or the bottom, even though the
            // pointer has a Y of its own.
            var r = RoiGesture.Resize(Roi(), RoiHandle.Left, 300, 999, FrameW, FrameH);

            Assert.Equal(300, r.OffsetY);
            Assert.Equal(100, r.Height);
            Assert.Equal(300, r.OffsetX);
            Assert.Equal(300, r.Width);          // right stays at 600
        }

        [Fact]
        public void Resize_Corner_MovesBothAxesAndPinsTheOppositeCorner()
        {
            var r = RoiGesture.Resize(Roi(), RoiHandle.TopLeft, 300, 200, FrameW, FrameH);

            Assert.Equal(300, r.OffsetX);
            Assert.Equal(200, r.OffsetY);
            Assert.Equal(600, r.OffsetX + r.Width);    // bottom-right pinned
            Assert.Equal(400, r.OffsetY + r.Height);
        }

        // ----- Dragging past the far side flips instead of going negative -----

        [Fact]
        public void Resize_PastTheOppositeEdge_FlipsRatherThanInverting()
        {
            // ChannelRoi reads a non-positive size as "the whole frame", so an inverted rectangle
            // would silently mean "measure everything" — the one outcome the operator cannot see.
            var r = RoiGesture.Resize(Roi(), RoiHandle.Left, 800, 350, FrameW, FrameH);

            Assert.Equal(600, r.OffsetX);        // the old right edge is now the left one
            Assert.Equal(200, r.Width);          // 800 - 600
            Assert.False(r.IsFullFrame);
        }

        // ----- Floors and walls -----

        [Fact]
        public void Resize_StopsAtTheMinimumSize()
        {
            var r = RoiGesture.Resize(Roi(), RoiHandle.Left, 599, 350, FrameW, FrameH);

            Assert.Equal(RoiGesture.MinSize, r.Width);
            Assert.Equal(600 - RoiGesture.MinSize, r.OffsetX);   // grew away from the pinned edge
        }

        [Fact]
        public void Resize_KeepsTheMinimumEvenAgainstTheFrameEdge()
        {
            // Pinned edge two pixels from the right wall: the rectangle cannot grow rightwards to
            // reach the minimum, so it has to come back off the wall instead.
            var narrow = new ChannelRoi(FrameW - 2, 300, 2, 100);

            var r = RoiGesture.Resize(narrow, RoiHandle.Left, FrameW - 1, 350, FrameW, FrameH);

            Assert.True(r.Width >= RoiGesture.MinSize);
            Assert.True(r.OffsetX + r.Width <= FrameW);
        }

        [Fact]
        public void Resize_CannotLeaveTheFrame()
        {
            var r = RoiGesture.Resize(Roi(), RoiHandle.BottomRight, 5000, 5000, FrameW, FrameH);

            Assert.True(r.OffsetX + r.Width <= FrameW);
            Assert.True(r.OffsetY + r.Height <= FrameH);
        }

        [Fact]
        public void Resize_LandsOnTheEvenGrid()
        {
            // The preview has to show what the commit will store, and the commit snaps to the CFA
            // grid. An odd preview means the rectangle jumps on release.
            var r = RoiGesture.Resize(Roi(), RoiHandle.BottomRight, 701, 451, FrameW, FrameH);

            Assert.Equal(0, r.OffsetX % 2);
            Assert.Equal(0, r.OffsetY % 2);
            Assert.Equal(0, r.Width % 2);
            Assert.Equal(0, r.Height % 2);
        }

        // ----- Moving -----

        [Fact]
        public void Move_ShiftsWithoutResizing()
        {
            var r = RoiGesture.Move(Roi(), 40, -60, FrameW, FrameH);

            Assert.Equal(440, r.OffsetX);
            Assert.Equal(240, r.OffsetY);
            Assert.Equal(200, r.Width);
            Assert.Equal(100, r.Height);
        }

        [Fact]
        public void Move_StopsAtTheFrameEdgeInsteadOfShrinking()
        {
            var r = RoiGesture.Move(Roi(), 5000, 5000, FrameW, FrameH);

            Assert.Equal(200, r.Width);          // size is preserved
            Assert.Equal(100, r.Height);
            Assert.Equal(FrameW - 200, r.OffsetX);
            Assert.Equal(FrameH - 100, r.OffsetY);
        }

        [Fact]
        public void Move_LeavesAFullFrameRoiAlone()
        {
            // Nothing to drag: full frame means "the whole thing", not a rectangle with a position.
            Assert.True(RoiGesture.Move(ChannelRoi.FullFrame, 40, 40, FrameW, FrameH).IsFullFrame);
        }
    }
}
