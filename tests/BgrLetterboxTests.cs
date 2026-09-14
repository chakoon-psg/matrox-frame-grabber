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

        private static void Planes(int w, int h, byte r, byte g, byte b,
                                   out byte[] pr, out byte[] pg, out byte[] pb)
        {
            pr = new byte[w * h]; pg = new byte[w * h]; pb = new byte[w * h];
            for (int i = 0; i < w * h; i++) { pr[i] = r; pg[i] = g; pb[i] = b; }
        }

        [Fact]
        public void Bands_land_in_bgr_order()
        {
            var lb = new BgrLetterbox(8, 8, 8, 8);
            Planes(8, 8, 10, 20, 30, out byte[] r, out byte[] g, out byte[] b);

            byte[] outp = lb.Fill(r, g, b, 8, 8);

            Assert.Equal(30, outp[0]);   // B
            Assert.Equal(20, outp[1]);   // G
            Assert.Equal(10, outp[2]);   // R
        }

        [Fact]
        public void The_pad_is_zero_and_the_picture_is_not()
        {
            BgrLetterbox lb = Real();
            Planes(SrcW, SrcH, 200, 200, 200, out byte[] r, out byte[] g, out byte[] b);

            byte[] outp = lb.Fill(r, g, b, SrcW, SrcH);

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
            var r = new byte[16];
            var g = new byte[16];
            var b = new byte[16];
            // Top-left quadrant: 0, 10, 20, 30 -> mean 15. Everything else 0.
            r[0] = 0; r[1] = 10; r[4] = 20; r[5] = 30;

            byte[] outp = lb.Fill(r, g, b, 4, 4);

            Assert.Equal(15, outp[2]);   // R of destination (0,0)
        }

        [Fact]
        public void A_frame_of_the_wrong_size_is_refused()
        {
            BgrLetterbox lb = Real();
            Planes(640, 480, 1, 1, 1, out byte[] r, out byte[] g, out byte[] b);

            Assert.Throws<ArgumentException>(() => lb.Fill(r, g, b, 640, 480));
        }

        [Fact]
        public void The_buffer_is_reused_rather_than_allocated()
        {
            BgrLetterbox lb = Real();
            Planes(SrcW, SrcH, 5, 5, 5, out byte[] r, out byte[] g, out byte[] b);

            byte[] first = lb.Fill(r, g, b, SrcW, SrcH);
            byte[] second = lb.Fill(r, g, b, SrcW, SrcH);

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
            Planes(sw, sh, 7, 8, 9, out byte[] r, out byte[] g, out byte[] b);

            byte[] outp = lb.Fill(r, g, b, sw, sh);

            Assert.Equal(mw * mh * 3, outp.Length);
            // The picture's first pixel carries the colour; that proves the fill ran.
            int first = ((lb.PadY * mw) + lb.PadX) * 3;
            Assert.Equal(9, outp[first]);
        }
    }
}
