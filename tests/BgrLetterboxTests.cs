using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The letterbox and its inverse.
    ///
    /// Most of these exist because a first draft got the inverse wrong in a way nothing would
    /// have caught: every coordinate off by up to 1.2 px, in the one feature whose whole output
    /// is coordinates.
    /// </summary>
    public class BgrLetterboxTests
    {
        // The real geometry: a decimated frame into this model's input.
        private const int SrcW = 1024, SrcH = 772, ModelW = 320, ModelH = 256;

        private static BgrLetterbox Real() => new BgrLetterbox(SrcW, SrcH, ModelW, ModelH);

        // ----- the geometry the design fixed -----

        [Fact]
        public void The_fit_matches_what_the_design_computed()
        {
            BgrLetterbox lb = Real();

            Assert.Equal(0.3125, lb.Scale, 6);
            Assert.Equal(320, lb.FittedWidth);
            Assert.Equal(241, lb.FittedHeight);      // round(772 * 0.3125) = round(241.25)
            Assert.Equal(0, lb.PadX);
            Assert.Equal(7, lb.PadY);                // (256 - 241) / 2, so 7 above and 8 below
        }

        /// <summary>
        /// The reason ScaleY exists. Inverting with the nominal Scale puts the frame's bottom
        /// edge at 771.2 instead of 772.
        /// </summary>
        [Fact]
        public void The_sampled_ratio_is_not_the_nominal_scale()
        {
            BgrLetterbox lb = Real();

            Assert.NotEqual(lb.Scale, lb.ScaleY, 5);
            Assert.Equal(241.0 / 772.0, lb.ScaleY, 9);

            double wrong = (lb.PadY + lb.FittedHeight - lb.PadY) / lb.Scale;
            double right = (lb.PadY + lb.FittedHeight - lb.PadY) / lb.ScaleY;
            Assert.InRange(wrong, 771.0, 771.5);     // what the first draft produced
            Assert.Equal(772.0, right, 6);           // what it has to be
        }

        /// <summary>Both ends, because padding is uneven and the centre would hide it.</summary>
        [Fact]
        public void Both_edges_of_the_picture_invert_to_the_frames_edges()
        {
            BgrLetterbox lb = Real();

            lb.ToFrame(lb.PadX, lb.PadY, lb.FittedWidth, lb.FittedHeight,
                       out float x, out float y, out float w, out float h);

            Assert.Equal(0f, x, 3);
            Assert.Equal(0f, y, 3);
            Assert.Equal(SrcW, w, 3);
            Assert.Equal(SrcH, h, 3);
        }

        /// <summary>
        /// Padding comes off before the division. The other order is off by PadY / Scale, which
        /// is 22 px here - large enough to put a finding on the wrong part of the screen.
        /// </summary>
        [Fact]
        public void Padding_is_subtracted_before_dividing()
        {
            BgrLetterbox lb = Real();

            lb.ToFrame(0f, lb.PadY, 1f, 1f, out _, out float y, out _, out _);
            Assert.Equal(0f, y, 3);

            double ifDividedFirst = lb.PadY / lb.ScaleY;
            Assert.InRange(ifDividedFirst, 20.0, 24.0);   // the error that order would cause
        }

        [Fact]
        public void Edges_clamp_to_the_size_not_to_size_minus_one()
        {
            BgrLetterbox lb = Real();

            // A box running well past the bottom right of the model input.
            lb.ToFrame(0f, 0f, ModelW * 2f, ModelH * 2f,
                       out float x, out float y, out float w, out float h);

            Assert.Equal(SrcW, x + w, 3);
            Assert.Equal(SrcH, y + h, 3);
        }

        [Fact]
        public void A_box_outside_the_picture_collapses_rather_than_going_negative()
        {
            BgrLetterbox lb = Real();

            lb.ToFrame(0f, 0f, 2f, 2f, out float x, out float y, out float w, out float h);

            Assert.True(x >= 0f && y >= 0f);
            Assert.True(w >= 0f && h >= 0f);
        }

        // ----- the fill -----

        /// <summary>
        /// A frame in the layout FrameExtractor produces: planar gbrp, three planes of w*h in
        /// the order G, B, R. Feeding the tests anything else would test a shape nothing sends.
        /// </summary>
        private static byte[] Frame(int w, int h, byte r, byte g, byte b)
        {
            int wh = w * h;
            var f = new byte[wh * 3];
            for (int i = 0; i < wh; i++)
            {
                f[i] = g;
                f[wh + i] = b;
                f[2 * wh + i] = r;
            }
            return f;
        }

        /// <summary>Sets one pixel of one plane, for the tests that check averaging.</summary>
        private static void SetR(byte[] frame, int w, int h, int x, int y, byte value) =>
            frame[2 * w * h + y * w + x] = value;

        [Fact]
        public void Bands_land_in_bgr_order()
        {
            var lb = new BgrLetterbox(8, 8, 8, 8);
            byte[] frame = Frame(8, 8, 10, 20, 30);

            byte[] outp = lb.Fill(frame, 8, 8, 0, 0);

            Assert.Equal(30, outp[0]);   // B
            Assert.Equal(20, outp[1]);   // G
            Assert.Equal(10, outp[2]);   // R
        }

        /// <summary>
        /// The region is cropped out of the whole frame, which is what the acquisition path
        /// hands over - one read shared by every consumer rather than a second read for this one.
        /// </summary>
        [Fact]
        public void The_region_is_cropped_out_of_the_shared_frame()
        {
            // A 16x16 frame that is dark everywhere except an 8x8 patch at 8,8.
            const int W = 16, H = 16;
            byte[] frame = Frame(W, H, 0, 0, 0);
            for (int y = 8; y < 16; y++)
                for (int x = 8; x < 16; x++)
                    SetR(frame, W, H, x, y, 200);

            var lb = new BgrLetterbox(8, 8, 8, 8);
            byte[] outp = lb.Fill(frame, W, H, 8, 8);

            // Every output pixel comes from inside the patch, so red is 200 throughout.
            for (int i = 0; i < 8 * 8; i++)
                Assert.Equal(200, outp[i * 3 + 2]);
        }

        [Fact]
        public void A_region_that_does_not_fit_the_frame_is_refused()
        {
            var lb = new BgrLetterbox(8, 8, 8, 8);
            byte[] frame = Frame(16, 16, 1, 1, 1);

            Assert.Throws<ArgumentException>(() => lb.Fill(frame, 16, 16, 12, 12));
            Assert.Throws<ArgumentException>(() => lb.Fill(frame, 16, 16, -1, 0));
        }

        [Fact]
        public void A_frame_shorter_than_three_planes_is_refused()
        {
            var lb = new BgrLetterbox(8, 8, 8, 8);

            Assert.Throws<ArgumentException>(() => lb.Fill(new byte[8 * 8 * 2], 8, 8, 0, 0));
        }

        [Fact]
        public void The_pad_is_zero_and_the_picture_is_not()
        {
            BgrLetterbox lb = Real();
            byte[] frame = Frame(SrcW, SrcH, 200, 200, 200);

            byte[] outp = lb.Fill(frame, SrcW, SrcH, 0, 0);

            // Row 0 is above the picture; row PadY is its first row.
            Assert.Equal(0, outp[0]);
            Assert.Equal(200, outp[(lb.PadY * ModelW) * 3]);

            // And the last padded row at the bottom, which is the one an even-split assumption
            // would get wrong.
            int lastRow = (ModelH - 1) * ModelW * 3;
            Assert.Equal(0, outp[lastRow]);
        }

        /// <summary>
        /// A box average sees every source pixel. A bilinear shrink of 3.2x would miss most of
        /// them - this checks the averaging actually happened rather than a sample being taken.
        /// </summary>
        [Fact]
        public void Shrinking_averages_rather_than_samples()
        {
            // 4x4 into 2x2: each destination pixel covers exactly four source pixels.
            var lb = new BgrLetterbox(4, 4, 2, 2);
            byte[] frame = Frame(4, 4, 0, 0, 0);
            // Top-left quadrant: 0, 10, 20, 30 -> mean 15. Everything else 0.
            SetR(frame, 4, 4, 0, 0, 0);
            SetR(frame, 4, 4, 1, 0, 10);
            SetR(frame, 4, 4, 0, 1, 20);
            SetR(frame, 4, 4, 1, 1, 30);

            byte[] outp = lb.Fill(frame, 4, 4, 0, 0);

            Assert.Equal(15, outp[2]);   // R of destination (0,0)
        }

        [Fact]
        public void The_buffer_is_reused_rather_than_allocated()
        {
            BgrLetterbox lb = Real();
            byte[] frame = Frame(SrcW, SrcH, 5, 5, 5);

            byte[] first = lb.Fill(frame, SrcW, SrcH, 0, 0);
            byte[] second = lb.Fill(frame, SrcW, SrcH, 0, 0);

            Assert.Same(first, second);
            Assert.Same(first, lb.Buffer);
        }

        /// <summary>
        /// Every destination pixel maps to at least one source pixel, at any shape. A gap would
        /// be a divide by zero; an overlap past the edge would be a read out of bounds.
        /// </summary>
        [Theory]
        [InlineData(1024, 772, 320, 256)]
        [InlineData(2064, 1544, 320, 256)]
        [InlineData(100, 100, 320, 256)]     // enlarging, not shrinking
        [InlineData(7, 3, 5, 5)]             // awkward and tiny
        public void Every_shape_fills_without_reading_out_of_bounds(int sw, int sh, int mw, int mh)
        {
            var lb = new BgrLetterbox(sw, sh, mw, mh);
            byte[] frame = Frame(sw, sh, 7, 8, 9);

            byte[] outp = lb.Fill(frame, sw, sh, 0, 0);

            Assert.Equal(mw * mh * 3, outp.Length);
            // The picture's first pixel carries the colour; that proves the fill ran.
            int first = ((lb.PadY * mw) + lb.PadX) * 3;
            Assert.Equal(9, outp[first]);
        }
    }
}
