using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The finder has one job and three ways to get it wrong: miss the screen, take the light the
    /// screen throws on the room with it, or return the union of two things that are not one
    /// thing. Each has a test, and so does the case each must not block.
    /// </summary>
    public class GreenScreenInspectorTests
    {
        private const int W = 64, H = 48;

        private static GreenScreenInspector Finder(double share = 0.42, int minLuma = 60,
                                                   double minArea = 0.004, double minFill = 0.70) =>
            new GreenScreenInspector(W, H, share, minLuma, minArea, minFill);

        /// <summary>A block of dark neutral pixels - what an unlit rig looks like.</summary>
        private static byte[] Dark()
        {
            var bgr = new byte[W * H * 3];
            for (int i = 0; i < W * H; i++)
            {
                bgr[i * 3] = 10; bgr[i * 3 + 1] = 11; bgr[i * 3 + 2] = 10;
            }
            return bgr;
        }

        /// <summary>Paints a rectangle in interleaved BGR.</summary>
        private static void Paint(byte[] bgr, int x0, int y0, int w, int h, byte b, byte g, byte r)
        {
            for (int y = y0; y < y0 + h && y < H; y++)
                for (int x = x0; x < x0 + w && x < W; x++)
                {
                    int o = (y * W + x) * 3;
                    bgr[o] = b; bgr[o + 1] = g; bgr[o + 2] = r;
                }
        }

        private static ScreenFinding First(GreenScreenInspector f, byte[] bgr, out int count)
        {
            var into = new ScreenFinding[8];
            count = f.Inspect(bgr, into);
            return count > 0 ? into[0] : default;
        }

        // ---- finding it -----------------------------------------------------------------

        [Fact]
        public void An_empty_dark_frame_finds_nothing()
        {
            Assert.Equal(0, Finder().Inspect(Dark(), new ScreenFinding[8]));
        }

        [Fact]
        public void A_green_rectangle_comes_back_with_its_own_edges()
        {
            byte[] bgr = Dark();
            Paint(bgr, 12, 9, 30, 20, 40, 200, 45);

            ScreenFinding f = First(Finder(), bgr, out int n);

            Assert.Equal(1, n);
            Assert.Equal(12f, f.X);
            Assert.Equal(9f, f.Y);
            Assert.Equal(30f, f.Width);
            Assert.Equal(20f, f.Height);
        }

        /// <summary>
        /// The size head on the learned model stopped growing at about a third of the frame.
        /// This is the case that motivated writing this class at all.
        /// </summary>
        [Fact]
        public void A_screen_filling_most_of_the_frame_is_not_clipped()
        {
            byte[] bgr = Dark();
            Paint(bgr, 2, 2, W - 4, H - 4, 40, 200, 45);

            ScreenFinding f = First(Finder(), bgr, out _);

            Assert.Equal(W - 4, (int)f.Width);
            Assert.Equal(H - 4, (int)f.Height);
        }

        [Fact]
        public void A_solid_rectangle_scores_one()
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 10, 20, 16, 40, 200, 45);

            ScreenFinding f = First(Finder(), bgr, out _);

            Assert.Equal(1.0f, f.Score, 3);
        }

        /// <summary>
        /// A screen with a dialog on it is still one screen. The dark patch breaks the fill but
        /// must not break the box.
        /// </summary>
        [Fact]
        public void A_dark_window_on_the_screen_does_not_split_it()
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 8, 30, 24, 40, 200, 45);
            Paint(bgr, 14, 12, 8, 6, 30, 30, 30);      // a dialog box on the panel

            ScreenFinding f = First(Finder(), bgr, out int n);

            Assert.Equal(1, n);
            Assert.Equal(30f, f.Width);
            Assert.Equal(24f, f.Height);
            Assert.True(f.Score < 1.0f, "the hole should show in the fill");
            Assert.True(f.Score > 0.8f, $"one small hole should not halve the fill, got {f.Score}");
        }

        // ---- not taking the room with it ------------------------------------------------

        /// <summary>
        /// The panel lights the bezel and the desk, and that light is green too. Only the emitter
        /// is bright enough, and that is the whole of the difference.
        /// </summary>
        [Fact]
        public void The_glow_a_screen_throws_is_not_part_of_the_screen()
        {
            byte[] bgr = Dark();
            Paint(bgr, 6, 4, 50, 40, 18, 34, 18);      // spill: green, but dim
            Paint(bgr, 20, 16, 16, 12, 40, 200, 45);   // the screen itself

            ScreenFinding f = First(Finder(), bgr, out _);

            Assert.Equal(20f, f.X);
            Assert.Equal(16f, f.Y);
            Assert.Equal(16f, f.Width);
            Assert.Equal(12f, f.Height);
        }

        [Fact]
        public void A_bright_neutral_rectangle_is_not_a_green_screen()
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 10, 20, 16, 200, 205, 200);

            Assert.Equal(0, Finder().Inspect(bgr, new ScreenFinding[8]));
        }

        [Fact]
        public void A_bright_blue_rectangle_is_not_a_green_screen()
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 10, 20, 16, 210, 90, 40);

            Assert.Equal(0, Finder().Inspect(bgr, new ScreenFinding[8]));
        }

        // ---- two things are two things ---------------------------------------------------

        [Fact]
        public void Two_separated_screens_are_two_findings_biggest_first()
        {
            byte[] bgr = Dark();
            Paint(bgr, 2, 4, 10, 8, 40, 200, 45);       // 80 px
            Paint(bgr, 40, 20, 20, 16, 40, 200, 45);    // 320 px

            var into = new ScreenFinding[8];
            int n = Finder().Inspect(bgr, into);

            Assert.Equal(2, n);
            Assert.Equal(40f, into[0].X);
            Assert.Equal(2f, into[1].X);
        }

        /// <summary>
        /// Two blobs touching only at a corner are two blobs. Eight-connectivity would weld a
        /// reflection to the screen, and a corner is exactly how a reflection touches.
        /// </summary>
        [Fact]
        public void Blobs_that_meet_at_a_corner_stay_apart()
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 10, 8, 8, 40, 200, 45);
            Paint(bgr, 18, 18, 8, 8, 40, 200, 45);

            var into = new ScreenFinding[8];
            int n = Finder().Inspect(bgr, into);

            Assert.Equal(2, n);
        }

        [Fact]
        public void A_speck_is_below_the_area_floor()
        {
            byte[] bgr = Dark();
            Paint(bgr, 30, 20, 2, 2, 40, 200, 45);      // 4 px of 3072

            Assert.Equal(0, Finder().Inspect(bgr, new ScreenFinding[8]));
        }

        // ---- shape of the answer ---------------------------------------------------------

        /// <summary>
        /// One threshold has to cover three cameras whose panels sit at very different levels,
        /// which is why the test is a share and not a level.
        /// </summary>
        [Theory]
        [InlineData((byte)18, (byte)90, (byte)20)]     // dim panel
        [InlineData((byte)40, (byte)200, (byte)45)]    // mid
        [InlineData((byte)60, (byte)250, (byte)70)]    // bright
        public void The_same_threshold_finds_panels_at_very_different_brightness(byte b, byte g, byte r)
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 10, 20, 16, b, g, r);

            ScreenFinding f = First(Finder(), bgr, out int n);

            Assert.Equal(1, n);
            Assert.Equal(20f, f.Width);
        }

        // ---- green is not the same as a screen -------------------------------------------

        /// <summary>
        /// A diagonal band is green, is big, and is not a screen. Content that merely happens to
        /// be green is the failure this rejects - the day the panel shows a map, the region must
        /// not follow the map.
        /// </summary>
        [Fact]
        public void A_ragged_green_shape_is_not_a_screen()
        {
            byte[] bgr = Dark();
            for (int y = 4; y < H - 4; y++)          // a diagonal stripe, 6 px wide
                Paint(bgr, 4 + y, y, 6, 1, 40, 200, 45);

            var into = new ScreenFinding[8];
            int n = Finder().Inspect(bgr, into);

            Assert.Equal(0, n);
        }

        /// <summary>
        /// The same shape gets through once the fill floor is lowered, which is what proves the
        /// rejection above came from the floor and not from the mask failing to see it at all.
        /// </summary>
        [Fact]
        public void That_same_shape_is_found_when_the_fill_floor_is_dropped()
        {
            byte[] bgr = Dark();
            for (int y = 4; y < H - 4; y++)
                Paint(bgr, 4 + y, y, 6, 1, 40, 200, 45);

            Assert.Equal(1, Finder(minFill: 0.05).Inspect(bgr, new ScreenFinding[8]));
        }

        /// <summary>
        /// A screen beside a ragged green shape: the shape is rejected and the screen is not, so
        /// the floor cannot be doing its work by simply dropping everything.
        /// </summary>
        [Fact]
        public void A_screen_survives_the_floor_that_rejects_the_shape()
        {
            byte[] bgr = Dark();
            for (int y = 4; y < 40; y++) Paint(bgr, 2 + y / 2, y, 4, 1, 40, 200, 45);
            Paint(bgr, 36, 10, 24, 20, 40, 200, 45);

            var into = new ScreenFinding[8];
            int n = Finder().Inspect(bgr, into);

            Assert.Equal(1, n);
            Assert.Equal(36f, into[0].X);
            Assert.Equal(24f, into[0].Width);
        }

        [Fact]
        public void It_writes_no_more_than_the_caller_asked_for()
        {
            byte[] bgr = Dark();
            Paint(bgr, 2, 2, 10, 8, 40, 200, 45);
            Paint(bgr, 20, 2, 10, 8, 40, 200, 45);
            Paint(bgr, 40, 2, 10, 8, 40, 200, 45);

            Assert.Equal(1, Finder().Inspect(bgr, new ScreenFinding[1]));
        }

        [Fact]
        public void A_short_buffer_is_refused_rather_than_read_past()
        {
            Assert.Throws<ArgumentException>(() =>
                Finder().Inspect(new byte[W * H * 3 - 1], new ScreenFinding[8]));
        }

        [Fact]
        public void Calling_it_twice_gives_the_same_answer()
        {
            byte[] bgr = Dark();
            Paint(bgr, 10, 10, 20, 16, 40, 200, 45);
            var f = Finder();

            var a = new ScreenFinding[8];
            var b = new ScreenFinding[8];
            int na = f.Inspect(bgr, a);
            int nb = f.Inspect(bgr, b);

            Assert.Equal(na, nb);
            Assert.Equal(a[0].X, b[0].X);
            Assert.Equal(a[0].Width, b[0].Width);
        }
    }
}
