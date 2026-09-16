using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Why auto-calibration only ever tightens.
    ///
    /// Dropout's floor is the loudest noise a run saw, so a quiet run measures a low floor. The
    /// two calibration hours on this rig make the point: 09-11 floored at 0.0289 with people
    /// about, and 09-13 floored at 0.0087 with the room empty. Twice the second is 0.017, and an
    /// hour like the first would then have produced false positives all night.
    /// </summary>
    public class AutoCalibrationLooseningTests
    {
        private const double Fps = 119.99;
        private static long Minutes(double m) => (long)(m * 60.0 * Fps);

        private static AutoCalibrationDecision Judge(double current, double floor,
                                                     double maxFactor = 2.0) =>
            new AutoCalibrationPolicy(true, 30.0, maxFactor)
                .Decide(current, floor, Minutes(60), Fps, 0, 0, 0, true);

        /// <summary>The real pair of hours, replayed.</summary>
        [Fact]
        public void The_quiet_sunday_may_not_undo_the_noisy_friday()
        {
            // Friday set it: 2 x 0.0289.
            AutoCalibrationDecision friday = Judge(current: 0.05, floor: 0.0289);
            Assert.True(friday.Adopted);
            Assert.Equal(0.0578, friday.To, 4);

            // Sunday measured a third of that floor. It must not follow it down.
            AutoCalibrationDecision sunday = Judge(current: 0.0578, floor: 0.0087);
            Assert.False(sunday.Adopted);
            Assert.Equal(0.0578, sunday.To, 4);
        }

        /// <summary>
        /// And it is refused for the right reason. A generous change cap would let the drop
        /// through, so the rule has to stand on its own rather than leaning on the cap.
        /// </summary>
        [Fact]
        public void Loosening_is_refused_on_its_own_terms_not_by_the_change_cap()
        {
            AutoCalibrationDecision d = Judge(current: 0.0578, floor: 0.0087, maxFactor: 100.0);

            Assert.Equal(AutoCalibrationVerdict.WouldLoosen, d.Verdict);
            Assert.Contains("person", d.Why);
        }

        /// <summary>A noisier run may still tighten, which is the direction that costs nothing.</summary>
        [Fact]
        public void A_noisier_run_may_still_raise_the_threshold()
        {
            AutoCalibrationDecision d = Judge(current: 0.0578, floor: 0.04);

            Assert.True(d.Adopted);
            Assert.Equal(0.08, d.To, 4);
        }

        /// <summary>
        /// Left to itself over many runs the threshold walks up to the worst hour it ever saw and
        /// stays there, which is the behaviour a false-positive budget wants.
        /// </summary>
        [Fact]
        public void Over_many_runs_it_settles_on_the_noisiest_hour()
        {
            double threshold = 0.03;
            double[] floors = { 0.0087, 0.0289, 0.0102, 0.0210, 0.0095, 0.0289, 0.0088 };

            foreach (double floor in floors)
            {
                AutoCalibrationDecision d = Judge(threshold, floor);
                if (d.Adopted) threshold = d.To;
            }

            Assert.Equal(0.0578, threshold, 4);      // twice the loudest floor in the set
        }
    }
}
