using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The locator's job is refusing to move, so that is what these test.
    ///
    /// Each interlock gets the case it was written for, and the case it must NOT block - a
    /// deadband that never lets anything through and a deadband that lets everything through
    /// are both "no frame lost, no error logged".
    /// </summary>
    public class RoiLocatorTests
    {
        private const int FrameW = 1024, FrameH = 772;

        // The rig's own region, Camera 0.
        private static ChannelRoi Panel => new ChannelRoi(296, 240, 284, 176);

        private static ScreenFinding[] One(float x, float y, float w, float h, float score = 0.9f) =>
            new[] { new ScreenFinding(AnomalyKind.Unknown, "panel", score, x, y, w, h) };

        /// <summary>
        /// A locator whose step limit is out of the way unless a test is about the step limit -
        /// the default 25% is deliberately restrictive, and leaving it on made three tests here
        /// assert the limit while claiming to be about something else.
        /// </summary>
        private static RoiLocator Locator(int dwell = 3, double step = 10.0) =>
            new RoiLocator(minScore: 0.5f, deadbandIou: 0.90, agreeIou: 0.70,
                           dwellLooks: dwell, maxStepFraction: step);

        // ---- T1: nothing found ----------------------------------------------------------

        [Fact]
        public void NothingFound_holds_the_region()
        {
            var loc = Locator();
            RoiLocatorDecision d = loc.Look(new ScreenFinding[4], 0, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.NothingFound, d.Outcome);
            Assert.Equal(Panel.OffsetX, d.Roi.OffsetX);
            Assert.Equal(Panel.Width, d.Roi.Width);
            Assert.Equal(0, loc.Moves);
        }

        [Fact]
        public void A_finding_under_the_score_is_not_a_finding()
        {
            var loc = Locator();
            RoiLocatorDecision d = loc.Look(One(100, 100, 200, 150, score: 0.31f), 1,
                                            Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.NothingFound, d.Outcome);
        }

        /// <summary>
        /// The reason this interlock exists: a panel that goes dark stops being found, and a
        /// region that cannot be found cannot be moved off the fault.
        /// </summary>
        [Fact]
        public void A_dark_panel_cannot_push_the_region_away()
        {
            var loc = Locator(dwell: 2);

            // Two looks that would have moved it, if anything had been found.
            loc.Look(new ScreenFinding[4], 0, Panel, FrameW, FrameH, false);
            RoiLocatorDecision d = loc.Look(new ScreenFinding[4], 0, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.NothingFound, d.Outcome);
            Assert.Equal(0, loc.Moves);
        }

        // ---- T2: a fault is running -----------------------------------------------------

        [Fact]
        public void A_running_fault_freezes_the_region_however_good_the_finding()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(600, 400, 284, 176), 1, Panel, FrameW, FrameH,
                                            faultRunning: true);

            Assert.Equal(RoiLocatorOutcome.Frozen, d.Outcome);
            Assert.Equal(Panel.OffsetX, d.Roi.OffsetX);
            Assert.Equal(0, loc.Moves);
        }

        [Fact]
        public void Confidence_collected_before_a_fault_is_dropped()
        {
            var loc = Locator(dwell: 2);
            ScreenFinding[] moved = One(600, 400, 284, 176);

            loc.Look(moved, 1, Panel, FrameW, FrameH, false);            // streak 1
            loc.Look(moved, 1, Panel, FrameW, FrameH, true);             // fault: dropped
            RoiLocatorDecision d = loc.Look(moved, 1, Panel, FrameW, FrameH, false);

            // Without the drop this third look would have been the second of the dwell and moved.
            Assert.Equal(RoiLocatorOutcome.Confirming, d.Outcome);
            Assert.Equal(1, d.Streak);
        }

        // ---- T3: dwell ------------------------------------------------------------------

        [Fact]
        public void One_look_is_never_enough()
        {
            var loc = Locator(dwell: 3);
            RoiLocatorDecision d = loc.Look(One(600, 400, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Confirming, d.Outcome);
            Assert.Equal(1, d.Streak);
        }

        [Fact]
        public void Consecutive_agreeing_looks_move_it()
        {
            var loc = Locator(dwell: 3);
            ScreenFinding[] moved = One(600, 400, 284, 176);

            Assert.Equal(RoiLocatorOutcome.Confirming, loc.Look(moved, 1, Panel, FrameW, FrameH, false).Outcome);
            Assert.Equal(RoiLocatorOutcome.Confirming, loc.Look(moved, 1, Panel, FrameW, FrameH, false).Outcome);
            RoiLocatorDecision d = loc.Look(moved, 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Moved, d.Outcome);
            Assert.Equal(600, d.Roi.OffsetX);
            Assert.Equal(400, d.Roi.OffsetY);
            Assert.Equal(1, loc.Moves);
        }

        /// <summary>
        /// Three boxes that each point somewhere different are three reasons not to move, not
        /// three votes. This is the case that separates a dwell from a counter.
        /// </summary>
        [Fact]
        public void Looks_that_disagree_with_each_other_never_accumulate()
        {
            var loc = Locator(dwell: 3);

            loc.Look(One(600, 400, 284, 176), 1, Panel, FrameW, FrameH, false);
            loc.Look(One(100, 100, 284, 176), 1, Panel, FrameW, FrameH, false);
            RoiLocatorDecision d = loc.Look(One(800, 500, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Confirming, d.Outcome);
            Assert.Equal(1, d.Streak);
            Assert.Equal(0, loc.Moves);
        }

        // ---- T4: deadband ---------------------------------------------------------------

        [Fact]
        public void A_box_where_the_region_already_is_does_nothing()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(296, 240, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.InDeadband, d.Outcome);
            Assert.Equal(0, loc.Moves);
        }

        [Fact]
        public void A_box_two_pixels_off_does_nothing_either()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(298, 242, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.InDeadband, d.Outcome);
        }

        /// <summary>
        /// The deadband must not swallow a real move. 40 px on a 284 px region is the rig being
        /// nudged, and it has to get through.
        /// </summary>
        [Fact]
        public void The_deadband_does_not_swallow_a_real_move()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(336, 280, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Moved, d.Outcome);
        }

        // ---- T5: step limit -------------------------------------------------------------

        [Fact]
        public void A_far_box_is_approached_rather_than_jumped_to()
        {
            var loc = Locator(dwell: 1, step: 0.25);      // 25% of 284 = 71 px per move
            RoiLocatorDecision d = loc.Look(One(700, 600, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Moved, d.Outcome);
            Assert.True(d.Roi.OffsetX < 700, $"stepped straight to {d.Roi.OffsetX}");
            Assert.True(d.Roi.OffsetX > Panel.OffsetX, "did not move at all");
            Assert.True(d.Roi.OffsetX - Panel.OffsetX <= 72, $"stepped {d.Roi.OffsetX - Panel.OffsetX} px");
        }

        [Fact]
        public void Repeated_looks_get_there_eventually()
        {
            var loc = Locator(dwell: 1, step: 0.25);
            ChannelRoi roi = Panel;
            ScreenFinding[] target = One(700, 600, 284, 176);

            for (int i = 0; i < 20; i++)
            {
                RoiLocatorDecision d = loc.Look(target, 1, roi, FrameW, FrameH, false);
                roi = d.Roi;
                if (d.Outcome == RoiLocatorOutcome.InDeadband) break;
            }

            Assert.InRange(roi.OffsetX, 690, 710);
            Assert.InRange(roi.OffsetY, 590, 610);
        }

        // ---- size versus position -------------------------------------------------------

        [Fact]
        public void Sliding_the_region_is_not_a_size_change()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(400, 340, 284, 176), 1, Panel, FrameW, FrameH, false);

            Assert.True(d.Moved);
            Assert.False(d.SizeChanged);
        }

        [Fact]
        public void Growing_the_region_is_reported_as_a_size_change()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(296, 240, 420, 260), 1, Panel, FrameW, FrameH, false);

            Assert.True(d.Moved);
            Assert.True(d.SizeChanged);
        }

        // ---- commissioning and the operator ---------------------------------------------

        /// <summary>
        /// With no rectangle drawn the region is the whole frame, and the first confident finding
        /// has to be able to establish one. A locator that treats "full frame" as "no region" can
        /// never start.
        /// </summary>
        [Fact]
        public void It_can_establish_a_region_from_full_frame()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(296, 240, 284, 176), 1,
                                            ChannelRoi.FullFrame, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Moved, d.Outcome);
            Assert.False(d.Roi.IsFullFrame);
        }

        [Fact]
        public void Reset_drops_what_was_being_confirmed()
        {
            var loc = Locator(dwell: 2);
            ScreenFinding[] moved = One(600, 400, 284, 176);

            loc.Look(moved, 1, Panel, FrameW, FrameH, false);
            loc.Reset();                                     // the operator drew a rectangle
            RoiLocatorDecision d = loc.Look(moved, 1, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Confirming, d.Outcome);
            Assert.Equal(1, d.Streak);
        }

        // ---- geometry -------------------------------------------------------------------

        [Fact]
        public void What_it_returns_is_already_snapped_to_the_even_grid()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(601, 401, 285, 177), 1, Panel, FrameW, FrameH, false);

            Assert.True(d.Moved);
            Assert.Equal(0, d.Roi.OffsetX % 2);
            Assert.Equal(0, d.Roi.OffsetY % 2);
            Assert.Equal(0, d.Roi.Width % 2);
            Assert.Equal(0, d.Roi.Height % 2);
        }

        [Fact]
        public void A_box_hanging_off_the_frame_is_clamped_inside_it()
        {
            var loc = Locator(dwell: 1);
            RoiLocatorDecision d = loc.Look(One(900, 700, 400, 300), 1, Panel, FrameW, FrameH, false);

            Assert.True(d.Roi.OffsetX >= 0 && d.Roi.OffsetY >= 0);
            Assert.True(d.Roi.OffsetX + d.Roi.Width <= FrameW);
            Assert.True(d.Roi.OffsetY + d.Roi.Height <= FrameH);
        }

        [Fact]
        public void The_highest_scoring_finding_wins_not_the_biggest()
        {
            var loc = Locator(dwell: 1);
            var findings = new[]
            {
                new ScreenFinding(AnomalyKind.Unknown, "reflection", 0.55f, 100, 100, 600, 400),
                new ScreenFinding(AnomalyKind.Unknown, "panel",      0.95f, 600, 400, 284, 176),
            };

            RoiLocatorDecision d = loc.Look(findings, 2, Panel, FrameW, FrameH, false);

            Assert.Equal(RoiLocatorOutcome.Moved, d.Outcome);
            Assert.Equal(600, d.Roi.OffsetX);
        }

        [Fact]
        public void Inset_trims_the_box_before_it_becomes_a_region()
        {
            var loose = new RoiLocator(dwellLooks: 1, maxStepFraction: 10.0, insetFraction: 0.0);
            var inset = new RoiLocator(dwellLooks: 1, maxStepFraction: 10.0, insetFraction: 0.10);

            ScreenFinding[] box = One(300, 300, 200, 100);
            ChannelRoi a = loose.Look(box, 1, Panel, FrameW, FrameH, false).Roi;
            ChannelRoi b = inset.Look(box, 1, Panel, FrameW, FrameH, false).Roi;

            Assert.Equal(200, a.Width);
            Assert.Equal(160, b.Width);      // 200 - 2 * 20
            Assert.Equal(80, b.Height);      // 100 - 2 * 10
        }

        [Fact]
        public void Overlap_is_zero_for_regions_that_do_not_touch()
        {
            Assert.Equal(0.0, RoiLocator.Overlap(new ChannelRoi(0, 0, 100, 100),
                                                 new ChannelRoi(200, 200, 100, 100)));
        }

        [Fact]
        public void Overlap_is_one_for_the_same_region()
        {
            Assert.Equal(1.0, RoiLocator.Overlap(Panel, Panel), 6);
        }
    }
}
