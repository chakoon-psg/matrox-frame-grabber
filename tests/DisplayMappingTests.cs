using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    public class DisplayMappingTests
    {
        [Fact]
        public void Create_CentresAnImageThatFits()
        {
            // Measured screenshot: control 650x245, frame 1024x772, zoom 0.25.
            var map = DisplayMapping.Create(650, 245, 1024, 772, 0.25, 0, 0);

            Assert.True(map.IsValid);
            Assert.Equal(0.25, map.Scale);
            Assert.Equal(197, map.OriginX);
            Assert.Equal(26, map.OriginY);
        }

        [Fact]
        public void Create_ZeroControlSizeIsInvalid()
        {
            Assert.False(DisplayMapping.Create(0, 245, 1024, 772, 0.25, 0, 0).IsValid);
            Assert.False(DisplayMapping.Create(650, 0, 1024, 772, 0.25, 0, 0).IsValid);
        }

        [Fact]
        public void Create_ZeroFrameSizeIsInvalid()
        {
            Assert.False(DisplayMapping.Create(650, 245, 0, 772, 0.25, 0, 0).IsValid);
            Assert.False(DisplayMapping.Create(650, 245, 1024, 0, 0.25, 0, 0).IsValid);
        }

        [Fact]
        public void Create_ZeroOrNegativeZoomIsInvalid()
        {
            Assert.False(DisplayMapping.Create(650, 245, 1024, 772, 0, 0, 0).IsValid);
            Assert.False(DisplayMapping.Create(650, 245, 1024, 772, -1, 0, 0).IsValid);
        }

        [Fact]
        public void Create_ZoomedInUsesMilsOffsets()
        {
            // The image (1024x772 at zoom 1.0) is larger than the control (650x245): MIL is
            // scrolled, and its offset describes where, not nothing.
            var map = DisplayMapping.Create(650, 245, 1024, 772, 1.0, 100, 50);

            Assert.True(map.IsValid);
            Assert.Equal(1.0, map.Scale);
            Assert.Equal(-100, map.OriginX);
            Assert.Equal(-50, map.OriginY);
        }

        [Fact]
        public void ToControlAndBack_RoundTrips()
        {
            var map = DisplayMapping.Create(650, 245, 1024, 772, 0.25, 0, 0);

            foreach (double v in new[] { 0.0, 1.0, 256.0, 511.5, 1023.0 })
            {
                Assert.Equal(v, map.ToImageX(map.ToControlX(v)), 6);
                Assert.Equal(v, map.ToImageY(map.ToControlY(v)), 6);
            }
        }

        [Fact]
        public void MeasuredFitCase_RoiLandsAtExpectedControlPosition()
        {
            // Camera 0, fit-to-window: control 650x245, frame 1024x772, zoom 0.25. An ROI whose
            // image-space origin is (256,192) should land at control x = 197 + 64 = 261.
            var map = DisplayMapping.Create(650, 245, 1024, 772, 0.25, 0, 0);

            Assert.Equal(261, map.ToControlX(256));
            Assert.Equal(74, map.ToControlY(192));
        }
    }
}
