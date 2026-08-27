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
            var snapped = new ChannelRoi(101, 203, 507, 609).Snap(MaxW, MaxH);
            Assert.Equal(100, snapped.OffsetX);
            Assert.Equal(202, snapped.OffsetY);
            Assert.Equal(506, snapped.Width);
            Assert.Equal(608, snapped.Height);
        }

        [Fact]
        public void Snap_ClampsSizeSoRoiStaysInsideSensor()
        {
            var snapped = new ChannelRoi(2000, 1500, 500, 500).Snap(MaxW, MaxH);
            Assert.True(snapped.OffsetX + snapped.Width <= MaxW);
            Assert.True(snapped.OffsetY + snapped.Height <= MaxH);
            Assert.Equal(64, snapped.Width);    // 2064 - 2000
            Assert.Equal(44, snapped.Height);   // 1544 - 1500
        }

        [Fact]
        public void Snap_ClampsNegativeOffsetToZero()
        {
            var snapped = new ChannelRoi(-50, -1, 800, 600).Snap(MaxW, MaxH);
            Assert.Equal(0, snapped.OffsetX);
            Assert.Equal(0, snapped.OffsetY);
        }

        [Fact]
        public void Snap_LeavesFullFrameAlone()
        {
            var snapped = ChannelRoi.FullFrame.Snap(MaxW, MaxH);
            Assert.True(snapped.IsFullFrame);
        }

        [Fact]
        public void Snap_OffsetPastSensorYieldsAtLeastOneIncrement()
        {
            // Degenerate input must not produce a zero or negative size, which MIL would reject.
            var snapped = new ChannelRoi(9999, 9999, 800, 600).Snap(MaxW, MaxH);
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
        public void MeasuredCeiling_NowAcceptsThreeFullFrameColourChannelsAt100Fps()
        {
            // What the x8 slot bought. At x4 this was 2.87 GB/s against a 1.7 ceiling — the
            // configuration the whole decimation and 1-band argument existed to avoid.
            double load = 3 * 100.0 * ChannelRoi.FullFrame.BytesPerFrame(3, MaxW, MaxH);
            Assert.True(load < ChannelRoi.HostDmaCeilingBytesPerSecond,
                $"expected under the ceiling, got {load / 1e9:F2} GB/s");
        }

        [Fact]
        public void MeasuredCeiling_AcceptsQuarterAreaColourAt184Fps()
        {
            var roi = new ChannelRoi(0, 0, 1030, 770);
            double load = 3 * 184.0 * roi.BytesPerFrame(3, MaxW, MaxH);
            Assert.True(load < ChannelRoi.WarnBytesPerSecond);
        }

        [Fact]
        public void ClampDecimation_AcceptsOnlyTheOfferedFactors()
        {
            Assert.Equal(1, ChannelRoi.ClampDecimation(1));
            Assert.Equal(2, ChannelRoi.ClampDecimation(2));
            Assert.Equal(4, ChannelRoi.ClampDecimation(4));
        }

        [Fact]
        public void ClampDecimation_FallsBackToFullResolution()
        {
            // A bad settings value must degrade to full resolution, not refuse to start.
            Assert.Equal(1, ChannelRoi.ClampDecimation(0));
            Assert.Equal(1, ChannelRoi.ClampDecimation(3));
            Assert.Equal(1, ChannelRoi.ClampDecimation(-2));
            Assert.Equal(1, ChannelRoi.ClampDecimation(int.MaxValue));
        }

        [Fact]
        public void DecimatedSize_HalvesAtFactorTwo()
        {
            Assert.Equal(1032, ChannelRoi.DecimatedWidth(MaxW, 2));
            Assert.Equal(772, ChannelRoi.DecimatedHeight(MaxH, 2));
            Assert.Equal(2064, ChannelRoi.DecimatedWidth(MaxW, 1));
            Assert.Equal(516, ChannelRoi.DecimatedWidth(MaxW, 4));
        }

        [Fact]
        public void MeasuredCeiling_AcceptsDecimationTwoColourAt184Fps()
        {
            // The configuration this whole plan exists to reach: 3 channels, colour, 184 fps.
            long bytes = (long)ChannelRoi.DecimatedWidth(MaxW, 2)
                       * ChannelRoi.DecimatedHeight(MaxH, 2) * 3;
            double load = 3 * 184.0 * bytes;
            Assert.True(load < ChannelRoi.WarnBytesPerSecond,
                $"expected under the warn threshold, got {load / 1e9:F2} GB/s");
        }

        // ----- Surviving a decimation change -----
        //
        // The ROI is stored in coordinates of the decimated frame, so changing the decimation
        // factor changes what those numbers point at. Reported from the field: two channels'
        // rectangles were drawn at decimation 1 (2064x1544) and then the channels were set to
        // decimation 2, halving the frame to 1024x772 — and the stored rectangles ended up
        // entirely outside it.

        [Fact]
        public void Rescale_KeepsTheRoiOnTheSamePartOfTheScene()
        {
            // The exact case from the field: CAM1's stored rectangle, drawn at decimation 1.
            var atDecim1 = new ChannelRoi(982, 796, 372, 240);

            var atDecim2 = atDecim1.Rescale(1, 2);

            // Half the coordinates, snapped down to the even grid.
            Assert.Equal(490, atDecim2.OffsetX);
            Assert.Equal(398, atDecim2.OffsetY);
            Assert.Equal(186, atDecim2.Width);
            Assert.Equal(120, atDecim2.Height);
        }

        [Fact]
        public void Rescale_LandsInsideTheNewFrame()
        {
            // 982 + 372 = 1354, which is past the 1024-wide frame decimation 2 delivers.
            var rescaled = new ChannelRoi(982, 796, 372, 240).Rescale(1, 2);

            Assert.True(rescaled.OffsetX + rescaled.Width <= 1024);
            Assert.True(rescaled.OffsetY + rescaled.Height <= 772);
        }

        [Fact]
        public void Rescale_IsReversibleWithinTheEvenGrid()
        {
            var original = new ChannelRoi(1000, 800, 400, 240);

            var roundTrip = original.Rescale(1, 2).Rescale(2, 1);

            Assert.Equal(original.OffsetX, roundTrip.OffsetX);
            Assert.Equal(original.OffsetY, roundTrip.OffsetY);
            Assert.Equal(original.Width, roundTrip.Width);
            Assert.Equal(original.Height, roundTrip.Height);
        }


        [Fact]
        public void Rescale_WillNotShrinkBelowTheEditableMinimum()
        {
            // Halving on every decimation change, with no floor, drove a field rectangle from
            // 504x308 down to 46x26 over a few toggles — a few pixels on screen, smaller than its
            // own handles, and read as "the ROI disappeared". Drags stop at the minimum; rescaling
            // has to as well.
            var roi = new ChannelRoi(100, 100, 96, 96);

            var shrunk = roi.Rescale(1, 2).Rescale(1, 2).Rescale(1, 2);

            Assert.True(shrunk.Width >= ChannelRoi.MinEditableSize, $"width {shrunk.Width}");
            Assert.True(shrunk.Height >= ChannelRoi.MinEditableSize, $"height {shrunk.Height}");
        }

        [Fact]
        public void Rescale_StillGrowsFreely()
        {
            // The floor must not interfere with the direction that makes the rectangle bigger.
            var grown = new ChannelRoi(100, 100, 96, 96).Rescale(2, 1);

            Assert.Equal(192, grown.Width);
            Assert.Equal(200, grown.OffsetX);
        }
        [Fact]
        public void Rescale_ToTheSameFactorChangesNothing()
        {
            var roi = new ChannelRoi(442, 380, 214, 148);

            Assert.Equal(roi.OffsetX, roi.Rescale(2, 2).OffsetX);
            Assert.Equal(roi.Height, roi.Rescale(2, 2).Height);
        }

        [Fact]
        public void Rescale_LeavesFullFrameAlone()
        {
            // Full frame means "whatever the frame is", so it needs no conversion.
            Assert.True(ChannelRoi.FullFrame.Rescale(1, 4).IsFullFrame);
        }

        [Fact]
        public void Rescale_IgnoresFactorsThisAppDoesNotOffer()
        {
            // A settings file could hold anything. An unrecognised factor must not silently
            // scale the rectangle by a garbage ratio.
            var roi = new ChannelRoi(400, 300, 200, 100);

            Assert.Equal(roi.OffsetX, roi.Rescale(3, 2).OffsetX);
            Assert.Equal(roi.OffsetX, roi.Rescale(2, 0).OffsetX);
        }
    }
}
