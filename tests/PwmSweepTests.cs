using System.Collections.Generic;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The exposure sweep decides the operating point for every channel, so the arithmetic that
    /// reads a verdict out of six numbers is worth checking without a panel in front of a camera.
    /// </summary>
    public class PwmSweepTests
    {
        static PwmPoint Panel(int us, float min, float max, float clip = 0) =>
            new PwmPoint("panel", us, min, max, (min + max) / 2, clip, 400);

        static PwmPoint Room(int us, float min, float max) =>
            new PwmPoint("room", us, min, max, (min + max) / 2, 0, 400);

        /// <summary>Ripple around a mean of 100: min/max 90/110 is 20%.</summary>
        static List<PwmPoint> Sweep(float ref6500, float r5000, float r8333, float r10000)
        {
            return new List<PwmPoint>
            {
                Panel(6500, 100 - ref6500 / 2, 100 + ref6500 / 2),
                Panel(5000, 100 - r5000 / 2, 100 + r5000 / 2),
                Panel(8333, 100 - r8333 / 2, 100 + r8333 / 2),
                Panel(10000, 100 - r10000 / 2, 100 + r10000 / 2),
            };
        }

        // ----- Ripple -----

        [Fact]
        public void RipplePercent_IsPeakToPeakOverTheMean()
        {
            Assert.Equal(20.0, PwmSweep.RipplePercent(90, 110), 3);
        }

        [Fact]
        public void RipplePercent_IsZeroForAFlatReading()
        {
            Assert.Equal(0.0, PwmSweep.RipplePercent(120, 120), 3);
        }

        [Fact]
        public void RipplePercent_IsZeroWhenThereIsNoLight()
        {
            // A covered lens must not produce a division by zero, or a huge ripple from noise
            // around a mean of nothing.
            Assert.Equal(0.0, PwmSweep.RipplePercent(0, 0), 3);
        }

        // ----- Deriving fps from exposure -----

        [Theory]
        [InlineData(8333, 119.4)]
        [InlineData(10000, 99.6)]
        [InlineData(5000, 198.2)]
        public void FpsForExposure_UsesTheCamerasOwnMaxRateRelation(int us, double expected)
        {
            Assert.Equal(expected, PwmSweep.FpsForExposure(us), 1);
        }

        [Fact]
        public void FpsForExposure_IsZeroForANonsenseExposure()
        {
            Assert.Equal(0.0, PwmSweep.FpsForExposure(0), 3);
        }

        // ----- The verdict -----

        [Fact]
        public void Judge_Sees120HzWhenOnly8333Nulls()
        {
            var v = PwmSweep.Judge(Sweep(ref6500: 12f, r5000: 10f, r8333: 0.5f, r10000: 11f));

            Assert.Equal(PwmFamily.Hz120, v.Family);
            Assert.Equal(8333, v.ExposureUs);
            Assert.Equal(119.4, v.TargetFps, 1);
        }

        [Fact]
        public void Judge_Sees100HzWhenBothOfItsPointsNull()
        {
            var v = PwmSweep.Judge(Sweep(ref6500: 12f, r5000: 0.4f, r8333: 11f, r10000: 0.5f));

            Assert.Equal(PwmFamily.Hz100, v.Family);
            // 10000 over 5000 where both null: the longer exposure gathers more light, and light
            // is what makes the reading precise.
            Assert.Equal(10000, v.ExposureUs);
            Assert.Equal(99.6, v.TargetFps, 1);
        }

        [Fact]
        public void Judge_ReportsNoneWhenNothingFallsFarEnough()
        {
            // Everything within a third of the reference is not a null, it is scatter.
            var v = PwmSweep.Judge(Sweep(ref6500: 12f, r5000: 10f, r8333: 9f, r10000: 11f));

            Assert.Equal(PwmFamily.None, v.Family);
        }

        [Fact]
        public void Judge_ReportsNoRippleWhenTheReferenceItselfIsFlat()
        {
            // Nothing to null. Recommending an exposure off this would be inventing one.
            var v = PwmSweep.Judge(Sweep(ref6500: 0.3f, r5000: 0.2f, r8333: 0.2f, r10000: 0.3f));

            Assert.Equal(PwmFamily.NoRipple, v.Family);
        }

        [Fact]
        public void Judge_ReportsNoneWhenTheReferencePointIsMissing()
        {
            // Without the reference there is no scale to call anything a null against.
            var v = PwmSweep.Judge(new List<PwmPoint> { Panel(8333, 99, 101) });

            Assert.Equal(PwmFamily.None, v.Family);
        }

        [Fact]
        public void Judge_FlagsClippingBecauseItFakesANull()
        {
            // A saturated frame has its ripple cut off at 255, so it reads flat while the panel is
            // still flickering — the most common way this measurement goes silently wrong.
            var points = Sweep(ref6500: 12f, r5000: 10f, r8333: 0.5f, r10000: 11f);
            points[2] = Panel(8333, 99.75f, 100.25f, clip: 4.2f);

            var v = PwmSweep.Judge(points);

            Assert.True(v.Clipped);
        }

        [Fact]
        public void Judge_IgnoresRoomPointsWhenReadingThePanelVerdict()
        {
            var points = Sweep(ref6500: 12f, r5000: 10f, r8333: 0.5f, r10000: 11f);
            points.Add(Room(6500, 10, 90));      // a huge ripple that must not change the verdict
            points.Add(Room(8333, 10, 90));

            Assert.Equal(PwmFamily.Hz120, PwmSweep.Judge(points).Family);
        }

        // ----- Room lighting -----

        [Fact]
        public void RoomNote_IdentifiesMainsRippleWhen8333NullsIt()
        {
            var points = new List<PwmPoint> { Room(6500, 90, 110), Room(8333, 99.8f, 100.2f) };

            Assert.Contains("120 Hz", PwmSweep.RoomNote(points));
        }

        [Fact]
        public void RoomNote_AsksForBlackoutWhenTheRippleSurvives()
        {
            var points = new List<PwmPoint> { Room(6500, 90, 110), Room(8333, 91, 109) };

            Assert.Contains("차광", PwmSweep.RoomNote(points));
        }

        [Fact]
        public void RoomNote_SaysNothingWasMeasuredWhenThePanelHalfRanAlone()
        {
            Assert.Contains("측정하지 않았", PwmSweep.RoomNote(Sweep(12f, 10f, 0.5f, 11f)));
        }

        // ----- CSV round trip -----

        [Fact]
        public void CsvRoundTrip_PreservesAPoint()
        {
            var original = new PwmPoint("panel", 8333, 99.5f, 100.5f, 100f, 0.25f, 431);

            Assert.True(PwmPoint.TryParseCsvLine(original.ToCsvLine(), out PwmPoint back));

            Assert.Equal(original.Label, back.Label);
            Assert.Equal(original.ExposureUs, back.ExposureUs);
            Assert.Equal(original.MinLuma, back.MinLuma, 3);
            Assert.Equal(original.MaxLuma, back.MaxLuma, 3);
            Assert.Equal(original.MaxClippedPct, back.MaxClippedPct, 3);
            Assert.Equal(original.Samples, back.Samples);
        }

        [Fact]
        public void ParseCsv_SkipsTheHeaderAndAnythingMalformed()
        {
            var lines = new[]
            {
                PwmSweep.CsvHeader,
                "panel,8333,99.5,100.5,100,0,431",
                "",
                "panel,not-a-number",
                "room,6500,90,110,100,0,400",
            };

            List<PwmPoint> points = PwmSweep.ParseCsv(lines);

            Assert.Equal(2, points.Count);
        }

        [Fact]
        public void ParseCsv_OfNothingIsEmptyRatherThanAThrow()
        {
            Assert.Empty(PwmSweep.ParseCsv(null));
        }

        // ----- The report -----

        [Fact]
        public void RenderReport_CarriesTheVerdictAndTheClippingWarning()
        {
            var points = Sweep(ref6500: 12f, r5000: 10f, r8333: 0.5f, r10000: 11f);
            points[2] = Panel(8333, 99.75f, 100.25f, clip: 4.2f);

            string report = PwmSweep.RenderReport(points, "Camera 0", "504x308 @ 912,736", "2026-08-28");

            Assert.Contains("120 Hz", report);
            Assert.Contains("8333 µs", report);
            Assert.Contains("신뢰할 수 없습니다", report);
            Assert.Contains("Camera 0", report);
        }
    }
}
