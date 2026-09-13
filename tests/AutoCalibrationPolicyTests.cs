using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The conditions that stand in for the person who is not at the bench.
    ///
    /// The manual path asks somebody to confirm the run was on a healthy panel. This tries to
    /// replace that judgement with checks a machine can make, and most of these tests are about
    /// what it must refuse.
    /// </summary>
    public class AutoCalibrationPolicyTests
    {
        private const double Fps = 119.99;
        private static long Minutes(double m) => (long)(m * 60.0 * Fps);

        private static AutoCalibrationPolicy On(double minMinutes = 30.0, double maxFactor = 2.0) =>
            new AutoCalibrationPolicy(true, minMinutes, maxFactor);

        /// <summary>The hour that was actually adopted by hand, replayed through the policy.</summary>
        private static AutoCalibrationDecision TheQuietHour(AutoCalibrationPolicy p,
                                                            double current = 0.05,
                                                            double floor = 0.0289) =>
            p.Decide(current, floor, Minutes(60), Fps,
                     anomalies: 0, rejectedForSpread: 0, framesMissed: 0, proposalUsable: true);

        // ----- the default -----

        [Fact]
        public void Off_is_the_default_answer_and_it_says_so()
        {
            var p = new AutoCalibrationPolicy(false, 30.0, 2.0);

            AutoCalibrationDecision d = TheQuietHour(p);

            Assert.False(d.Adopted);
            Assert.Equal(AutoCalibrationVerdict.SwitchedOff, d.Verdict);
            Assert.Equal(0.05, d.To, 4);              // unchanged
            Assert.Contains("declined", d.ToString());
        }

        // ----- the run that should be adopted -----

        /// <summary>
        /// The 2026-09-11 hour: 432,000 frames, nothing fired, nothing missed, floor 0.0289. It
        /// was adopted by hand at 0.058, and the policy has to reach the same number.
        /// </summary>
        [Fact]
        public void A_quiet_hour_adopts_twice_its_floor()
        {
            AutoCalibrationDecision d = TheQuietHour(On());

            Assert.True(d.Adopted);
            Assert.Equal(0.0578, d.To, 4);            // 2 x 0.0289, which is what a person chose
            Assert.Equal(0.0289, d.Floor, 4);
            Assert.Contains("adopted", d.ToString());
        }

        /// <summary>
        /// Not the histogram proposal. On that hour the proposal was 0.030, which is 1.04x the
        /// loudest noise the run measured - correct statistics, no margin.
        /// </summary>
        [Fact]
        public void It_adopts_the_margin_and_not_the_budget_line()
        {
            AutoCalibrationDecision d = TheQuietHour(On());

            Assert.Equal(AutoCalibrationPolicy.FloorMultiple, d.To / d.Floor, 3);
            Assert.True(d.To > 0.030, "0.030 was the proposal and it leaves 4% of headroom");
        }

        // ----- what it must refuse -----

        /// <summary>
        /// The one that keeps the measurement honest. Frames inside an event are excluded from the
        /// floor, and which frames those are depends on the threshold being set - so a run with an
        /// event in it could ratchet the threshold up, run after run, until the detector is deaf.
        /// </summary>
        [Fact]
        public void A_run_with_anything_in_it_is_refused_and_the_reason_is_the_ratchet()
        {
            var p = On();

            AutoCalibrationDecision d = p.Decide(0.05, 0.0289, Minutes(60), Fps,
                anomalies: 1, rejectedForSpread: 0, framesMissed: 0, proposalUsable: true);

            Assert.False(d.Adopted);
            Assert.Equal(AutoCalibrationVerdict.NotQuiet, d.Verdict);
            Assert.Contains("depends on the threshold", d.Why);
            Assert.Equal(0.05, d.To, 4);
        }

        [Fact]
        public void Frames_turned_away_for_onset_spread_count_as_not_quiet()
        {
            AutoCalibrationDecision d = On().Decide(0.05, 0.0289, Minutes(60), Fps,
                anomalies: 0, rejectedForSpread: 4, framesMissed: 0, proposalUsable: true);

            Assert.Equal(AutoCalibrationVerdict.NotQuiet, d.Verdict);
        }

        /// <summary>
        /// Five minutes resolves the budget only to 11.8/hour against a target of 1. A run that
        /// short measures a quieter rig than the one that has to survive the night.
        /// </summary>
        [Fact]
        public void A_short_run_is_refused_however_clean_it_was()
        {
            AutoCalibrationDecision d = On(minMinutes: 30.0).Decide(0.05, 0.0289, Minutes(5), Fps,
                anomalies: 0, rejectedForSpread: 0, framesMissed: 0, proposalUsable: true);

            Assert.False(d.Adopted);
            Assert.Equal(AutoCalibrationVerdict.TooShort, d.Verdict);
            Assert.Contains("5.0 min", d.Why);
        }

        [Fact]
        public void A_run_that_lost_frames_does_not_describe_the_rig()
        {
            AutoCalibrationDecision d = On().Decide(0.05, 0.0289, Minutes(60), Fps,
                anomalies: 0, rejectedForSpread: 0, framesMissed: 3, proposalUsable: true);

            Assert.Equal(AutoCalibrationVerdict.Degraded, d.Verdict);
        }

        /// <summary>
        /// A short run fails both checks, and the duration is the one an operator can act on.
        /// Reporting "nothing to propose from" for a run that was simply too short sends the
        /// reader looking for a fault in the panel.
        /// </summary>
        [Fact]
        public void A_short_run_is_told_it_was_short_and_not_that_it_was_empty()
        {
            AutoCalibrationDecision d = On(minMinutes: 30.0).Decide(0.05, 0.0289, Minutes(0.5), Fps,
                anomalies: 0, rejectedForSpread: 0, framesMissed: 0, proposalUsable: false);

            Assert.Equal(AutoCalibrationVerdict.TooShort, d.Verdict);
        }

        [Fact]
        public void Nothing_to_propose_from_is_refused()
        {
            AutoCalibrationDecision d = On().Decide(0.05, 0.0289, Minutes(60), Fps,
                anomalies: 0, rejectedForSpread: 0, framesMissed: 0, proposalUsable: false);

            Assert.Equal(AutoCalibrationVerdict.NoProposal, d.Verdict);
        }

        [Fact]
        public void A_floor_of_zero_is_not_a_measurement()
        {
            AutoCalibrationDecision d = On().Decide(0.05, 0.0, Minutes(60), Fps,
                anomalies: 0, rejectedForSpread: 0, framesMissed: 0, proposalUsable: true);

            Assert.Equal(AutoCalibrationVerdict.NoProposal, d.Verdict);
        }

        // ----- the size of the move -----

        /// <summary>
        /// A big jump is a changed rig - a light moved, an exposure changed, a camera came loose -
        /// and those are worth a person looking rather than following wherever the rig went.
        /// </summary>
        [Fact]
        public void A_move_larger_than_the_cap_is_refused_in_either_direction()
        {
            var p = On(maxFactor: 2.0);

            // Floor collapsed: 0.15 -> 0.0158 would be a 9.5x drop.
            AutoCalibrationDecision down = p.Decide(0.15, 0.0079, Minutes(60), Fps, 0, 0, 0, true);
            Assert.Equal(AutoCalibrationVerdict.TooBigAChange, down.Verdict);
            Assert.Equal(0.15, down.To, 4);

            // Floor exploded: 0.02 -> 0.20 would be a 10x rise.
            AutoCalibrationDecision up = p.Decide(0.02, 0.10, Minutes(60), Fps, 0, 0, 0, true);
            Assert.Equal(AutoCalibrationVerdict.TooBigAChange, up.Verdict);
        }

        [Fact]
        public void A_move_inside_the_cap_goes_through()
        {
            // 0.05 -> 0.08 is 1.6x, inside a cap of 2.
            AutoCalibrationDecision d = On(maxFactor: 2.0)
                .Decide(0.05, 0.04, Minutes(60), Fps, 0, 0, 0, true);

            Assert.True(d.Adopted);
            Assert.Equal(0.08, d.To, 4);
        }

        /// <summary>
        /// Without this every run rewrites the file with a value a fraction of a percent away, and
        /// "which run set this" stops meaning anything.
        /// </summary>
        [Fact]
        public void A_move_too_small_to_matter_is_left_alone()
        {
            // 2 x 0.0290 = 0.0580 against a current 0.0578: 0.3%.
            AutoCalibrationDecision d = On().Decide(0.0578, 0.0290, Minutes(60), Fps, 0, 0, 0, true);

            Assert.False(d.Adopted);
            Assert.Equal(AutoCalibrationVerdict.NotWorthIt, d.Verdict);
            Assert.Equal(0.0578, d.To, 4);
        }

        /// <summary>
        /// The ratchet, simulated. Feed the policy its own output as the new threshold on a rig
        /// whose floor never changes, and it has to stop after the first move.
        /// </summary>
        [Fact]
        public void Repeated_runs_on_an_unchanged_rig_settle_instead_of_walking()
        {
            var p = On();
            double threshold = 0.05;
            const double floor = 0.0289;
            int moves = 0;

            for (int run = 0; run < 20; run++)
            {
                AutoCalibrationDecision d = p.Decide(threshold, floor, Minutes(60), Fps, 0, 0, 0, true);
                if (!d.Adopted) continue;
                threshold = d.To;
                moves++;
            }

            Assert.Equal(1, moves);
            Assert.Equal(0.0578, threshold, 4);
        }

        // ----- bounds -----

        [Fact]
        public void The_knobs_are_clamped_to_something_sane()
        {
            var tiny = new AutoCalibrationPolicy(true, minMinutes: -5.0, maxChangeFactor: 1.0);
            Assert.True(tiny.MinMinutes >= 1.0);
            Assert.True(tiny.MaxChangeFactor > 1.0);

            var huge = new AutoCalibrationPolicy(true, minMinutes: 99999.0, maxChangeFactor: 9999.0);
            Assert.True(huge.MinMinutes <= 1440.0);
            Assert.True(huge.MaxChangeFactor <= 100.0);
        }

        [Fact]
        public void Every_refusal_says_why()
        {
            var p = On();
            var refusals = new[]
            {
                new AutoCalibrationPolicy(false, 30, 2).Decide(0.05, 0.0289, Minutes(60), Fps, 0, 0, 0, true),
                p.Decide(0.05, 0.0289, Minutes(1), Fps, 0, 0, 0, true),
                p.Decide(0.05, 0.0289, Minutes(60), Fps, 1, 0, 0, true),
                p.Decide(0.05, 0.0289, Minutes(60), Fps, 0, 0, 1, true),
                p.Decide(0.05, 0.0289, Minutes(60), Fps, 0, 0, 0, false),
                p.Decide(0.15, 0.0079, Minutes(60), Fps, 0, 0, 0, true),
            };

            foreach (AutoCalibrationDecision d in refusals)
            {
                Assert.False(d.Adopted);
                Assert.False(string.IsNullOrWhiteSpace(d.Why), $"{d.Verdict} gave no reason");
                Assert.Equal(d.From, d.To);          // and it changed nothing
            }
        }
    }
}
