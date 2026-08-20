using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    public class ChannelRoiTests
    {
        const int MaxW = 2064;
        const int MaxH = 1544;

        [Fact]
        public void FullFrame_IsFullFrame()
        {
            Assert.True(ChannelRoi.FullFrame.IsFullFrame);
            Assert.True(new ChannelRoi(0, 0, 0, 0).IsFullFrame);
        }

        [Fact]
        public void ZeroOrNegativeSize_MeansFullFrame()
        {
            Assert.True(new ChannelRoi(100, 100, 0, 500).IsFullFrame);
            Assert.True(new ChannelRoi(100, 100, 500, -1).IsFullFrame);
        }

        [Fact]
        public void Snap_RoundsOddCoordinatesDownToEven()
        {
            // Increment 1 from the camera must still be forced to 2: an odd offset shifts the
            // Bayer CFA phase and the colours come out swapped.
            var snapped = new ChannelRoi(101, 203, 507, 609).Snap(1, 1, 1, 1, MaxW, MaxH);
            Assert.Equal(100, snapped.OffsetX);
            Assert.Equal(202, snapped.OffsetY);
            Assert.Equal(506, snapped.Width);
            Assert.Equal(608, snapped.Height);
        }

        [Fact]
        public void Snap_HonoursLargerHardwareIncrement()
        {
            var snapped = new ChannelRoi(100, 100, 1000, 1000).Snap(16, 8, 16, 8, MaxW, MaxH);
            Assert.Equal(96, snapped.OffsetX);    // 100 -> down to multiple of 16
            Assert.Equal(96, snapped.OffsetY);    // 100 -> down to multiple of 8
            Assert.Equal(992, snapped.Width);     // 1000 -> down to multiple of 16
            Assert.Equal(1000, snapped.Height);   // already a multiple of 8
        }

        [Fact]
        public void Snap_ClampsSizeSoRoiStaysInsideSensor()
        {
            var snapped = new ChannelRoi(2000, 1500, 500, 500).Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.True(snapped.OffsetX + snapped.Width <= MaxW);
            Assert.True(snapped.OffsetY + snapped.Height <= MaxH);
            Assert.Equal(64, snapped.Width);    // 2064 - 2000
            Assert.Equal(44, snapped.Height);   // 1544 - 1500
        }

        [Fact]
        public void Snap_ClampsNegativeOffsetToZero()
        {
            var snapped = new ChannelRoi(-50, -1, 800, 600).Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.Equal(0, snapped.OffsetX);
            Assert.Equal(0, snapped.OffsetY);
        }

        [Fact]
        public void Snap_LeavesFullFrameAlone()
        {
            var snapped = ChannelRoi.FullFrame.Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.True(snapped.IsFullFrame);
        }

        [Fact]
        public void Snap_OffsetPastSensorYieldsAtLeastOneIncrement()
        {
            // Degenerate input must not produce a zero or negative size, which MIL would reject.
            var snapped = new ChannelRoi(9999, 9999, 800, 600).Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.True(snapped.Width >= 2);
            Assert.True(snapped.Height >= 2);
            Assert.True(snapped.OffsetX + snapped.Width <= MaxW);
            Assert.True(snapped.OffsetY + snapped.Height <= MaxH);
        }

        [Fact]
        public void BytesPerFrame_FullFrameUsesSensorSize()
        {
            Assert.Equal(2064L * 1544 * 3, ChannelRoi.FullFrame.BytesPerFrame(3, MaxW, MaxH));
        }

        [Fact]
        public void BytesPerFrame_CroppedUsesRoiSize()
        {
            Assert.Equal(1024L * 772 * 3, new ChannelRoi(0, 0, 1024, 772).BytesPerFrame(3, MaxW, MaxH));
        }

        [Fact]
        public void MeasuredCeiling_RejectsThreeFullFrameColourChannelsAt184Fps()
        {
            // The measurement this whole plan exists for: 3 x 184 x 9.12 MB = 5.0 GB/s.
            double load = 3 * 184.0 * ChannelRoi.FullFrame.BytesPerFrame(3, MaxW, MaxH);
            Assert.True(load > ChannelRoi.HostDmaCeilingBytesPerSecond);
        }

        [Fact]
        public void MeasuredCeiling_AcceptsQuarterAreaColourAt184Fps()
        {
            var roi = new ChannelRoi(0, 0, 1030, 770);
            double load = 3 * 184.0 * roi.BytesPerFrame(3, MaxW, MaxH);
            Assert.True(load < ChannelRoi.WarnBytesPerSecond);
        }
    }
}
