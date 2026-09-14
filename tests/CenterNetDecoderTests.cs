using System;
using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The decode arithmetic, which is the half of a learned inspector that can be tested at all.
    ///
    /// In the project this was ported from, the same arithmetic sat beside the inference session
    /// and had no tests. Pulling it into a class with no ONNX type in it is most of the value of
    /// the port.
    /// </summary>
    public class CenterNetDecoderTests
    {
        private static PeakCandidate Box(float logit, float x, float y, float w, float h,
                                         int row = 0, int col = 0, int channel = 0) =>
            new PeakCandidate(row, col, channel, logit, x, y, w, h);

        // ----- sigmoid and the logit threshold -----

        [Fact]
        public void Sigmoid_is_stable_at_both_extremes()
        {
            Assert.Equal(0.5f, PeakCandidate.Sigmoid(0f), 5);
            Assert.InRange(PeakCandidate.Sigmoid(-100f), 0f, 1e-6f);
            Assert.InRange(PeakCandidate.Sigmoid(100f), 1f - 1e-6f, 1f);

            // The naive form overflows here; this one must not produce NaN or infinity.
            Assert.False(float.IsNaN(PeakCandidate.Sigmoid(-800f)));
            Assert.False(float.IsInfinity(PeakCandidate.Sigmoid(-800f)));
        }

        /// <summary>
        /// Thresholding in logit space has to be the same decision as thresholding after a
        /// sigmoid - that equivalence is the only reason it is allowed to skip the sigmoid.
        /// </summary>
        [Theory]
        [InlineData(0.1f)]
        [InlineData(0.3f)]
        [InlineData(0.5f)]
        [InlineData(0.9f)]
        public void A_logit_threshold_decides_the_same_as_a_probability_threshold(float t)
        {
            float cut = PeakCandidate.LogitOf(t);

            for (float logit = -8f; logit <= 8f; logit += 0.05f)
            {
                bool byLogit = logit >= cut;
                bool byProbability = PeakCandidate.Sigmoid(logit) >= t;
                // Only the boundary itself may disagree, and only by floating-point rounding.
                if (MathF.Abs(logit - cut) > 1e-3f)
                    Assert.Equal(byProbability, byLogit);
            }
        }

        // ----- IoU -----

        [Fact]
        public void Two_boxes_that_only_touch_do_not_overlap()
        {
            // Edges lie between pixels: 0..10 and 10..20 share an edge, not an area.
            Assert.Equal(0f, CenterNetDecoder.Iou(Box(1f, 0, 0, 10, 10), Box(1f, 10, 0, 10, 10)), 6);
        }

        [Fact]
        public void A_box_fully_inside_another_gives_the_ratio_of_areas()
        {
            float iou = CenterNetDecoder.Iou(Box(1f, 0, 0, 10, 10), Box(1f, 0, 0, 5, 5));
            Assert.Equal(25f / 100f, iou, 5);
        }

        [Fact]
        public void The_same_box_is_one()
        {
            Assert.Equal(1f, CenterNetDecoder.Iou(Box(1f, 3, 4, 7, 8), Box(1f, 3, 4, 7, 8)), 5);
        }

        [Fact]
        public void A_degenerate_box_does_not_divide_by_zero()
        {
            Assert.Equal(0f, CenterNetDecoder.Iou(Box(1f, 0, 0, 0, 0), Box(1f, 0, 0, 0, 0)), 6);
        }

        // ----- suppression -----

        [Fact]
        public void The_strongest_box_survives_and_its_overlap_does_not()
        {
            var d = new CenterNetDecoder(scoreThreshold: 0.3f, iouThreshold: 0.5f);
            var input = new List<PeakCandidate>
            {
                Box(1.0f, 0, 0, 10, 10),
                Box(2.0f, 1, 1, 10, 10),      // stronger, overlapping heavily
            };

            Span<PeakCandidate> into = stackalloc PeakCandidate[8];
            int n = d.Suppress(input, into);

            Assert.Equal(1, n);
            Assert.Equal(2.0f, into[0].Logit, 5);
        }

        [Fact]
        public void Boxes_that_barely_overlap_both_survive()
        {
            var d = new CenterNetDecoder(iouThreshold: 0.5f);
            var input = new List<PeakCandidate>
            {
                Box(2.0f, 0, 0, 10, 10),
                Box(1.0f, 9, 0, 10, 10),     // IoU about 0.05
            };

            Span<PeakCandidate> into = stackalloc PeakCandidate[8];
            Assert.Equal(2, d.Suppress(input, into));
        }

        /// <summary>Two classes may be in the same place; one must not delete the other.</summary>
        [Fact]
        public void A_box_of_another_class_is_not_suppressed()
        {
            var d = new CenterNetDecoder(iouThreshold: 0.5f);
            var input = new List<PeakCandidate>
            {
                Box(2.0f, 0, 0, 10, 10, channel: 0),
                Box(1.0f, 0, 0, 10, 10, channel: 1),
            };

            Span<PeakCandidate> into = stackalloc PeakCandidate[8];
            Assert.Equal(2, d.Suppress(input, into));
        }

        /// <summary>
        /// The reason ties are broken by position.
        ///
        /// A flat heatmap region puts one logit on several neighbouring cells. If the choice is
        /// left to the sort, which cell wins can change between frames and a stationary box
        /// jitters. Feeding the same candidates in different orders has to give the same answer.
        /// </summary>
        [Fact]
        public void Equal_scores_resolve_the_same_way_whatever_order_they_arrive_in()
        {
            // Boxes apart from one another, so suppression keeps all three and the only thing
            // under test is the order. Identical boxes would have IoU 1.0 and delete each other,
            // which is correct behaviour and would hide what this test is for.
            var d = new CenterNetDecoder(iouThreshold: 0.5f);
            var a = new List<PeakCandidate>
            {
                Box(1.0f, 0, 0, 4, 4, row: 5, col: 9),
                Box(1.0f, 100, 0, 4, 4, row: 5, col: 3),
                Box(1.0f, 200, 0, 4, 4, row: 2, col: 7),
            };
            var b = new List<PeakCandidate> { a[2], a[0], a[1] };

            Span<PeakCandidate> x = stackalloc PeakCandidate[8];
            Span<PeakCandidate> y = stackalloc PeakCandidate[8];
            int nx = d.Suppress(a, x);
            int ny = d.Suppress(b, y);

            Assert.Equal(nx, ny);
            for (int i = 0; i < nx; i++)
            {
                Assert.Equal(x[i].Row, y[i].Row);
                Assert.Equal(x[i].Column, y[i].Column);
            }
            // And the order is the documented one: by row, then column.
            Assert.Equal(2, x[0].Row);
            Assert.Equal(5, x[1].Row);
            Assert.Equal(3, x[1].Column);
        }

        [Fact]
        public void No_more_than_the_cap_comes_back()
        {
            var d = new CenterNetDecoder(iouThreshold: 0.99f, maxFindings: 3);
            var input = new List<PeakCandidate>();
            for (int i = 0; i < 20; i++)
                input.Add(Box(i, i * 100, 0, 10, 10, row: i));

            Span<PeakCandidate> into = stackalloc PeakCandidate[32];
            Assert.Equal(3, d.Suppress(input, into));
        }

        [Fact]
        public void The_destination_being_smaller_than_the_result_is_not_an_error()
        {
            var d = new CenterNetDecoder(iouThreshold: 0.99f);
            var input = new List<PeakCandidate>
            {
                Box(3f, 0, 0, 5, 5, row: 0),
                Box(2f, 100, 0, 5, 5, row: 1),
                Box(1f, 200, 0, 5, 5, row: 2),
            };

            Span<PeakCandidate> into = stackalloc PeakCandidate[2];
            Assert.Equal(2, d.Suppress(input, into));
            Assert.Equal(3f, into[0].Logit, 5);       // still strongest first
        }

        [Fact]
        public void Nothing_in_nothing_out()
        {
            var d = new CenterNetDecoder();
            Span<PeakCandidate> into = stackalloc PeakCandidate[4];

            Assert.Equal(0, d.Suppress(new List<PeakCandidate>(), into));
            Assert.Equal(0, d.Suppress(null, into));
        }
    }
}
