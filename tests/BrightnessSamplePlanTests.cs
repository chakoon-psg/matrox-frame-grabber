using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The brightness reading describes the analysis ROI, or the whole frame when none was drawn.
    /// Which rows and columns that turns into is decided here rather than in the meter, because the
    /// meter needs MIL and so cannot be checked without a board.
    /// </summary>
    public class BrightnessSamplePlanTests
    {
        // The frame this camera delivers at decimation 2, and at decimation 1.
        const int FrameW = 1024;
        const int FrameH = 772;

        // ----- No rectangle drawn: measure everything -----

        [Fact]
        public void FullFrameRoi_CoversTheWholeFrame()
        {
            var r = BrightnessSamplePlan.For(ChannelRoi.FullFrame, FrameW, FrameH);

            Assert.False(r.IsEmpty);
            Assert.Equal(0, r.OffsetX);
            Assert.Equal(0, r.OffsetY);
            Assert.Equal(FrameW, r.Width);
            Assert.Equal(FrameH, r.Height);
        }

        [Fact]
        public void FullFrameRoi_KeepsTheStripLayoutTheMeterAlreadyUsed()
        {
            // 16 strips, spaced (772-4)/15 = 51. This is the layout that was in the meter before the
            // ROI existed; changing it would silently move every historical reading's basis.
            var r = BrightnessSamplePlan.For(ChannelRoi.FullFrame, FrameW, FrameH);

            Assert.Equal(16, r.Strips);
            Assert.Equal(51, r.Step);
        }

        [Fact]
        public void EveryStripFitsInsideTheRegion()
        {
            var r = BrightnessSamplePlan.For(ChannelRoi.FullFrame, 2064, 1544);

            for (int s = 0; s < r.Strips; s++)
            {
                Assert.True(r.StripTop(s) >= r.OffsetY);
                Assert.True(r.StripTop(s) + BrightnessSamplePlan.RowsPerStrip <= r.OffsetY + r.Height);
            }
        }

        // ----- A rectangle drawn: measure only that -----

        [Fact]
        public void Roi_SamplesInsideTheRectangleOnly()
        {
            var r = BrightnessSamplePlan.For(new ChannelRoi(400, 300, 200, 100), FrameW, FrameH);

            Assert.Equal(400, r.OffsetX);
            Assert.Equal(300, r.OffsetY);
            Assert.Equal(200, r.Width);
            Assert.Equal(100, r.Height);
        }

        [Fact]
        public void Roi_SpreadsStripsDownTheRectangleNotTheFrame()
        {
            var r = BrightnessSamplePlan.For(new ChannelRoi(400, 300, 200, 100), FrameW, FrameH);

            Assert.Equal(16, r.Strips);
            Assert.Equal(300, r.StripTop(0));                       // starts at the ROI's top
            int last = r.StripTop(r.Strips - 1) + BrightnessSamplePlan.RowsPerStrip;
            Assert.True(last <= 400);                               // never past the ROI's bottom
            Assert.True(last >= 394);                               // and reaches near it
        }

        [Fact]
        public void Roi_AtTheFrameCornerStaysInsideTheBuffer()
        {
            // Reading past the buffer is a MIL error, not merely a wrong number.
            var r = BrightnessSamplePlan.For(new ChannelRoi(FrameW - 64, FrameH - 64, 64, 64),
                                             FrameW, FrameH);

            Assert.True(r.OffsetX + r.Width <= FrameW);
            Assert.True(r.StripTop(r.Strips - 1) + BrightnessSamplePlan.RowsPerStrip <= FrameH);
        }

        // ----- Rectangles that no longer fit the frame -----

        [Fact]
        public void Roi_LargerThanTheFrame_IsClampedNotTrusted()
        {
            // A settings file written at decimation 1 and read back at decimation 2 holds a
            // rectangle twice too big for the buffer.
            var r = BrightnessSamplePlan.For(new ChannelRoi(900, 700, 400, 400), FrameW, FrameH);

            Assert.False(r.IsEmpty);
            Assert.Equal(FrameW - 900, r.Width);
            Assert.Equal(FrameH - 700, r.Height);
        }

        [Fact]
        public void Roi_EntirelyOutsideTheFrame_MeasuresNothing()
        {
            var r = BrightnessSamplePlan.For(new ChannelRoi(FrameW, FrameH, 100, 100),
                                             FrameW, FrameH);

            Assert.True(r.IsEmpty);
        }

        [Fact]
        public void Roi_TooShortForOneStrip_MeasuresNothingRatherThanTheWholeFrame()
        {
            // Falling back to the frame would keep the graph moving while measuring something the
            // operator never asked for — the one outcome they cannot see. A gap is visible.
            var r = BrightnessSamplePlan.For(new ChannelRoi(400, 300, 200, 3), FrameW, FrameH);

            Assert.True(r.IsEmpty);
        }

        [Fact]
        public void EmptyFrame_MeasuresNothing()
        {
            Assert.True(BrightnessSamplePlan.For(ChannelRoi.FullFrame, 0, 0).IsEmpty);
        }

        // ----- Short regions -----

        [Theory]
        [InlineData(4)]      // exactly one strip
        [InlineData(16)]     // the smallest rectangle the operator can draw
        [InlineData(63)]
        public void ShortRoi_TakesFewerStripsRatherThanRepeatingRows(int height)
        {
            var r = BrightnessSamplePlan.For(new ChannelRoi(400, 300, 200, height), FrameW, FrameH);

            Assert.False(r.IsEmpty);
            Assert.True(r.Strips >= 1);

            // Strips must not overlap: an overlap would count the same rows twice and report a
            // coverage larger than what was actually read.
            for (int s = 1; s < r.Strips; s++)
                Assert.True(r.StripTop(s) >= r.StripTop(s - 1) + BrightnessSamplePlan.RowsPerStrip);

            int last = r.StripTop(r.Strips - 1) + BrightnessSamplePlan.RowsPerStrip;
            Assert.True(last <= 300 + height);
        }

        [Fact]
        public void MinimumDrawableRoi_StillCoversItsWholeHeight()
        {
            // 16 rows / 4 per strip = 4 strips at 0, 4, 8, 12 — the whole rectangle, no gaps.
            var r = BrightnessSamplePlan.For(
                new ChannelRoi(400, 300, ChannelRoi.MinEditableSize, ChannelRoi.MinEditableSize),
                FrameW, FrameH);

            Assert.Equal(4, r.Strips);
            Assert.Equal(4, r.Step);
            Assert.Equal(316, r.StripTop(r.Strips - 1) + BrightnessSamplePlan.RowsPerStrip);
        }
    }
}
