using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The class that keeps an image library out of the build. Everything an inspector needs
    /// before the model - de-planarising, scaling, padding, and the way back - is here, so this
    /// is where it gets tested.
    /// </summary>
    public class BgrLetterboxTests
    {
        // The operating point: decimation 2, three bands, and the model the analysis measured.
        private const int SrcW = 1024, SrcH = 772, OutW = 320, OutH = 256;

        private static BgrLetterbox AtOperatingPoint() => new BgrLetterbox(SrcW, SrcH, OutW, OutH);

        /// <summary>A planar G,B,R frame where every pixel is the same colour.</summary>
        private static byte[] Flat(int w, int h, byte g, byte b, byte r)
        {
            var f = new byte[w * h * 3];
            int plane = w * h;
            for (int i = 0; i < plane; i++) { f[i] = g; f[plane + i] = b; f[plane * 2 + i] = r; }
            return f;
        }

        // ----- geometry -----

        /// <summary>
        /// The numbers the ADR quotes. If these move, the document is wrong and so is anything
        /// that was tuned against it.
        /// </summary>
        [Fact]
        public void The_operating_point_maps_as_the_design_says()
        {
            var lb = AtOperatingPoint();

            Assert.Equal(0.3125, lb.Scale, 4);      // 320/1024 is smaller than 256/772
            Assert.Equal(320, lb.ScaledWidth);
            Assert.Equal(241, lb.ScaledHeight);     // 772 x 0.3125 = 241.25
            Assert.Equal(0, lb.PadX);
            Assert.Equal(7, lb.PadY);               // (256 - 241) / 2
        }

        [Fact]
        public void The_whole_frame_fits_and_the_aspect_survives()
        {
            var lb = AtOperatingPoint();

            Assert.True(lb.ScaledWidth <= lb.OutputWidth);
            Assert.True(lb.ScaledHeight <= lb.OutputHeight);
            double srcAspect = SrcW / (double)SrcH;
            double dstAspect = lb.ScaledWidth / (double)lb.ScaledHeight;
            Assert.Equal(srcAspect, dstAspect, 2);
        }

        /// <summary>Padding on the other axis, so the arithmetic is not only ever exercised one way.</summary>
        [Fact]
        public void A_tall_frame_pads_at_the_sides()
        {
            var lb = new BgrLetterbox(400, 800, 200, 200);

            Assert.Equal(0.25, lb.Scale, 4);
            Assert.Equal(100, lb.ScaledWidth);
            Assert.Equal(200, lb.ScaledHeight);
            Assert.Equal(50, lb.PadX);
            Assert.Equal(0, lb.PadY);
        }

        // ----- the pixels -----

        /// <summary>
        /// Planes in, pixels out: the source is G,B,R in three blocks and the picture is B,G,R per
        /// pixel. Getting this backwards gives a model a colour-swapped world and it will still
        /// return plausible-looking boxes, which is what makes it worth a test.
        /// </summary>
        [Fact]
        public void Planar_gbr_becomes_interleaved_bgr()
        {
            var lb = new BgrLetterbox(4, 4, 4, 4);

            Assert.True(lb.FillFromPlanar(Flat(4, 4, g: 10, b: 20, r: 30)));

            for (int p = 0; p < 16; p++)
            {
                Assert.Equal(20, lb.Picture[p * 3]);       // B
                Assert.Equal(10, lb.Picture[p * 3 + 1]);   // G
                Assert.Equal(30, lb.Picture[p * 3 + 2]);   // R
            }
        }

        [Fact]
        public void The_padding_is_black_and_the_picture_is_not()
        {
            var lb = AtOperatingPoint();
            Assert.True(lb.FillFromPlanar(Flat(SrcW, SrcH, g: 200, b: 200, r: 200)));

            // Row 0 is padding (PadY = 7); row 7 is the first row of the reduced frame.
            for (int x = 0; x < OutW; x++)
                Assert.Equal(0, lb.Picture[(0 * OutW + x) * 3]);
            for (int x = 0; x < OutW; x++)
                Assert.Equal(200, lb.Picture[(lb.PadY * OutW + x) * 3]);
        }

        /// <summary>
        /// Box average, not a sample. A source that is half black and half white must come back
        /// grey where a destination pixel spans both - a bilinear or nearest reduction would give
        /// one of the two and alias a fine pattern away.
        /// </summary>
        [Fact]
        public void Reduction_averages_the_area_it_covers()
        {
            // 4x1 source, alternating black and white columns, reduced to 2x1.
            var src = new byte[4 * 1 * 3];
            int plane = 4;
            for (int x = 0; x < 4; x++)
            {
                byte v = (byte)(x % 2 == 0 ? 0 : 255);
                src[x] = v; src[plane + x] = v; src[plane * 2 + x] = v;
            }

            var lb = new BgrLetterbox(4, 1, 2, 1);
            Assert.True(lb.FillFromPlanar(src));

            // Each destination pixel covers one black and one white source pixel.
            Assert.Equal(127, lb.Picture[0]);
            Assert.Equal(127, lb.Picture[3]);
        }

        [Fact]
        public void A_frame_of_the_wrong_size_is_refused_rather_than_read_past()
        {
            var lb = AtOperatingPoint();

            Assert.False(lb.FillFromPlanar(null));
            Assert.False(lb.FillFromPlanar(new byte[SrcW * SrcH * 3 - 1]));
            Assert.True(lb.FillFromPlanar(new byte[SrcW * SrcH * 3]));
        }

        // ----- the way back -----

        /// <summary>
        /// The edges map exactly, which only holds if the inverse uses the per-axis ratio the
        /// fill samples at. With the nominal 0.3125 the bottom of the frame comes back as 771.2
        /// instead of 772 - this test is how that was found.
        /// </summary>
        [Fact]
        public void A_coordinate_comes_back_to_the_frame_it_came_from()
        {
            var lb = AtOperatingPoint();

            // Both edges of the reduced frame, exactly.
            Assert.Equal(0.0, lb.ToSourceY(lb.PadY), 6);
            Assert.Equal(SrcH, lb.ToSourceY(lb.PadY + lb.ScaledHeight), 6);
            Assert.Equal(0.0, lb.ToSourceX(lb.PadX), 6);
            Assert.Equal(SrcW, lb.ToSourceX(lb.PadX + lb.ScaledWidth), 6);

            // And the middle of the reduced frame is the middle of the frame. Note this is not
            // the middle of the picture: padding is 7 above and 8 below, so they differ by half
            // a picture pixel.
            Assert.Equal(SrcH / 2.0, lb.ToSourceY(lb.PadY + lb.ScaledHeight / 2.0), 6);
            Assert.Equal(SrcW / 2.0, lb.ToSourceX(lb.PadX + lb.ScaledWidth / 2.0), 6);
        }

        /// <summary>The nominal scale is not the mapping, and the difference is measurable.</summary>
        [Fact]
        public void The_axis_ratios_are_what_map_not_the_nominal_scale()
        {
            var lb = AtOperatingPoint();

            Assert.Equal(0.3125, lb.Scale, 6);
            Assert.Equal(320 / 1024.0, lb.ScaleX, 6);      // exact here
            Assert.Equal(241 / 772.0, lb.ScaleY, 6);       // 0.31218, not 0.3125
            Assert.NotEqual(lb.Scale, lb.ScaleY, 5);

            // What the nominal scale would have given for the bottom edge.
            Assert.Equal(771.2, (lb.PadY + lb.ScaledHeight - lb.PadY) / lb.Scale, 1);
        }

        [Fact]
        public void A_finding_maps_back_whole()
        {
            var lb = AtOperatingPoint();
            // A box covering the middle quarter of the reduced frame.
            var picture = new ScreenFinding(AnomalyKind.Dropout, 0.9f,
                                            80f, lb.PadY + 60f, 160f, 120f);

            ScreenFinding onFrame = lb.ToSource(picture);

            Assert.Equal(AnomalyKind.Dropout, onFrame.Kind);
            Assert.Equal(0.9f, onFrame.Score);
            Assert.Equal(256f, onFrame.X, 0);          // 80 / 0.3125
            Assert.Equal(192f, onFrame.Y, 0);          // 60 / 0.3125
            Assert.Equal(512f, onFrame.Width, 0);
            Assert.Equal(384f, onFrame.Height, 0);
        }

        /// <summary>
        /// Clamped to the frame, and to width rather than width-1: the right edge of a 1024-wide
        /// frame is 1024, and shaving a pixel there is an error nobody sees until they measure.
        /// </summary>
        [Fact]
        public void A_finding_over_the_edge_is_clamped_to_the_frame()
        {
            var lb = AtOperatingPoint();
            var spilling = new ScreenFinding(AnomalyKind.Blackout, 0.5f,
                                             -40f, -40f, OutW + 200f, OutH + 200f);

            ScreenFinding onFrame = lb.ToSource(spilling);

            Assert.Equal(0f, onFrame.X);
            Assert.Equal(0f, onFrame.Y);
            Assert.Equal(SrcW, onFrame.Right, 0);
            Assert.Equal(SrcH, onFrame.Bottom, 0);
        }

        [Fact]
        public void It_says_what_geometry_it_is_for()
        {
            Assert.Contains("1024x772 -> 320x241", AtOperatingPoint().Describe());
        }

        [Fact]
        public void A_letterbox_needs_real_dimensions()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BgrLetterbox(0, 10, 10, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BgrLetterbox(10, 10, 10, 0));
        }
    }
}
