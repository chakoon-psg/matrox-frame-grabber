using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The scan measures the whole ripple-vs-exposure curve instead of testing two hypotheses, so
    /// the frequency has to be read off the curve and the operating point computed from it. Both
    /// are arithmetic, and both decide what every channel runs at — worth checking without a panel.
    /// </summary>
    public class PwmScanTests
    {
        /// <summary>
        /// A synthetic scan of a backlight at <paramref name="freqHz"/>. Ripple follows the box
        /// integral's own response, |sinc(pi*f*T)|, so the nulls land exactly where the real thing
        /// would put them.
        /// </summary>
        static List<PwmPoint> Curve(double freqHz, float amplitudePct = 12f)
        {
            var points = new List<PwmPoint>();
            foreach (int us in PwmSweep.ScanExposuresUs)
            {
                double x = System.Math.PI * freqHz * us / 1e6;
                double sinc = System.Math.Abs(x) < 1e-9 ? 1.0 : System.Math.Abs(System.Math.Sin(x) / x);
                double ripple = amplitudePct * sinc;
                float mean = 100f;
                points.Add(new PwmPoint("panel", us,
                    (float)(mean - ripple / 2), (float)(mean + ripple / 2), mean, 0, 400,
                    PwmSweep.FpsForExposure(us)));
            }
            return points;
        }

        // ----- Reading nulls off the curve -----

        [Fact]
        public void FindNulls_LandsOnTheMultiplesOfThePeriod()
        {
            // 200 Hz: period 5000 us, so nulls at 5000 and 10000 inside the 4000-11000 scan.
            List<PwmSweep.PwmNull> nulls = PwmSweep.FindNulls(Curve(200));

            Assert.Equal(2, nulls.Count);
            Assert.Equal(5000, nulls[0].ExposureUs);
            Assert.Equal(10000, nulls[1].ExposureUs);
        }

        [Fact]
        public void FindNulls_ReturnsNothingForAFlatCurve()
        {
            // DC dimming, or a saturated frame: no ripple anywhere, so nothing to null.
            Assert.Empty(PwmSweep.FindNulls(Curve(200, amplitudePct: 0.2f)));
        }

        [Theory]
        [InlineData(120, 120)]
        [InlineData(200, 200)]
        [InlineData(240, 240)]
        [InlineData(300, 300)]
        [InlineData(480, 480)]
        public void InferFrequency_RecoversWhatTheCurveWasBuiltFrom(double actual, double expected)
        {
            // Within 1%: the nulls are only known to the scan's 250 us grid, so the frequency
            // inherits that quantisation. 1% is far tighter than picking the wrong k would be.
            double got = PwmSweep.InferFrequencyHz(Curve(actual));
            Assert.True(System.Math.Abs(got - expected) / expected < 0.01,
                        $"expected ~{expected} Hz, got {got:0.#} Hz");
        }

        [Theory]
        [InlineData(100)]
        [InlineData(120)]
        public void InferFrequency_ResolvesALoneNullFromWhatTheScanDidNotFind(double freq)
        {
            // Only one null falls inside 4000-11000 for these two. That looks ambiguous — the
            // exposure is consistent with k/f for every k — but k=2 would put a second null at half
            // the exposure, which is inside the range and absent from it. Ruling out every k whose
            // neighbour should have shown up leaves exactly one answer.
            Assert.Single(PwmSweep.FindNulls(Curve(freq)));

            double got = PwmSweep.InferFrequencyHz(Curve(freq));
            Assert.True(System.Math.Abs(got - freq) / freq < 0.01,
                        $"expected ~{freq} Hz, got {got:0.#} Hz");
        }

        [Fact]
        public void InferFrequency_IsZeroWhenThereIsNoCurve()
        {
            Assert.Equal(0, PwmSweep.InferFrequencyHz(new List<PwmPoint>()));
        }

        // ----- Turning a frequency into an operating point -----

        [Fact]
        public void OperatingPoints_At240Hz_OffersTheWholeMultipleInsideTheWindow()
        {
            List<PwmSweep.OperatingPoint> pts = PwmSweep.OperatingPoints(240);
            PwmSweep.OperatingPoint best = pts.Find(p => p.InWindow);

            Assert.Equal(8333, best.ExposureUs);      // 2 x 4167
            Assert.Equal(119.4, best.Fps, 1);
            Assert.Equal(0, best.DeadFraction, 3);    // exposure sets the rate, so no blind gap
        }

        [Fact]
        public void OperatingPoints_At200Hz_HasNothingInTheWindow()
        {
            // The awkward case: k=1 is 5000 us and overruns the camera, k=2 is 10000 us and drops
            // under the floor. Presenting either as a free choice would hide a forced trade.
            List<PwmSweep.OperatingPoint> pts = PwmSweep.OperatingPoints(200);

            Assert.DoesNotContain(pts, p => p.InWindow);
            Assert.Contains(pts, p => p.ExposureUs == 5000 && p.DeadFraction > 0.05);
            Assert.Contains(pts, p => p.ExposureUs == 10000 && p.DeadFraction == 0);
        }

        [Fact]
        public void OperatingPoints_CountsTheBlindGapWhenTheRateHasToBeCapped()
        {
            // 5000 us allows 198 fps; held to 184 the gap is 435 of every 5435 us.
            PwmSweep.OperatingPoint p =
                PwmSweep.OperatingPoints(200).Find(x => x.ExposureUs == 5000);

            Assert.Equal(0.080, p.DeadFraction, 2);
            Assert.Equal(184, p.Fps, 1);
        }

        [Fact]
        public void OperatingPoints_AreLongestFirstWithinEachGroup()
        {
            // Longest first because light is the scarce thing — but usable candidates ahead of
            // unusable ones, so the top row of the table is always the recommendation.
            List<PwmSweep.OperatingPoint> pts = PwmSweep.OperatingPoints(480);

            bool seenOutOfWindow = false;
            for (int i = 0; i < pts.Count; i++)
            {
                if (!pts[i].InWindow) seenOutOfWindow = true;
                else Assert.False(seenOutOfWindow, "a usable candidate came after an unusable one");

                if (i > 0 && pts[i - 1].InWindow == pts[i].InWindow)
                    Assert.True(pts[i - 1].ExposureUs > pts[i].ExposureUs);
            }

            Assert.True(pts[0].InWindow);
            Assert.Equal(8333, pts[0].ExposureUs);
        }

        [Fact]
        public void OperatingPoints_ReportBMinFromTheExposure()
        {
            // b_min = the depth threshold x exposure. Written against the constant rather than a
            // literal: the threshold is a measured value that has already moved once, and the
            // relationship is what this test is about, not the number it currently produces.
            PwmSweep.OperatingPoint p =
                PwmSweep.OperatingPoints(240).Find(x => x.ExposureUs == 8333);

            Assert.Equal(PwmSweep.DepthThreshold * 8.333, p.BMinMs, 3);
        }

        [Fact]
        public void OperatingPoints_FollowTheMeasuredCapRatherThanTheModelNumber()
        {
            // The 184 in the model name is a full-resolution figure. If the camera reports it can
            // do 300 at this decimation, 5000 us stops needing a cap and its blind gap disappears.
            PwmSweep.OperatingPoint p =
                PwmSweep.OperatingPoints(200, fpsCap: 300).Find(x => x.ExposureUs == 5000);

            Assert.True(p.InWindow);
            Assert.Equal(0, p.DeadFraction, 3);
        }

        [Fact]
        public void OperatingPoints_OfNothingIsEmptyRatherThanAThrow()
        {
            Assert.Empty(PwmSweep.OperatingPoints(0));
        }

        // ----- The scan list itself -----

        [Fact]
        public void ScanExposures_CoverTheRangeAtAResolutionThatSeparatesNulls()
        {
            int[] scan = PwmSweep.ScanExposuresUs;

            Assert.Equal(4000, scan[0]);
            Assert.Equal(11000, scan[scan.Length - 1]);
            Assert.Equal(29, scan.Length);
            // 250 us steps resolve a period of 2000 us — 500 Hz — with eight samples between nulls.
            Assert.Equal(250, scan[1] - scan[0]);
        }

        // ----- The four-point sweep must not be read as a curve -----

        [Fact]
        public void FindNulls_IgnoresTheFourPointSweep()
        {
            // Four unevenly spaced probes also have a lowest point, but calling it a null would let
            // the report announce a frequency read off four samples — the over-reach the scan mode
            // exists to replace.
            var sweep = new List<PwmPoint>
            {
                new PwmPoint("panel", 6500, 96f, 104f, 100f, 0, 400),
                new PwmPoint("panel", 5000, 96f, 104f, 100f, 0, 400),
                new PwmPoint("panel", 8333, 99.8f, 100.2f, 100f, 0, 400),
                new PwmPoint("panel", 10000, 96f, 104f, 100f, 0, 400),
            };

            Assert.Empty(PwmSweep.FindNulls(sweep));
            Assert.Equal(0, PwmSweep.InferFrequencyHz(sweep));
        }

        [Fact]
        public void OperatingPoints_PutUsableCandidatesFirst()
        {
            // Sorting purely by exposure would head the table with an unusable row, which reads as
            // the recommendation. 120 Hz: 8333 us is usable, 16667 us is under the floor.
            List<PwmSweep.OperatingPoint> pts = PwmSweep.OperatingPoints(120);

            Assert.True(pts[0].InWindow);
            Assert.Equal(8333, pts[0].ExposureUs);
        }

        // ----- Carrying the camera's own rate through the CSV -----

        [Fact]
        public void CsvRoundTrip_KeepsTheCameraReportedRate()
        {
            var original = new PwmPoint("panel", 8333, 99.5f, 100.5f, 100f, 0f, 431, 119.4);

            Assert.True(PwmPoint.TryParseCsvLine(original.ToCsvLine(), out PwmPoint back));
            Assert.Equal(119.4, back.ResultingFps, 2);
        }

        [Fact]
        public void ParseCsv_StillReadsRowsWrittenBeforeTheRateColumnExisted()
        {
            // Old files have seven fields. They must not be dropped as malformed.
            List<PwmPoint> points = PwmSweep.ParseCsv(new[] { "panel,8333,99.5,100.5,100,0,431" });

            Assert.Single(points);
            Assert.Equal(0, points[0].ResultingFps);
        }
    }
}
