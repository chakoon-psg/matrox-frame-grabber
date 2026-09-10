using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The lane's status dot. It exists because events alone miss the two worst problems: a channel
    /// losing frames and a channel whose detector never judges them both report nothing, and
    /// nothing is what a healthy panel reports.
    /// </summary>
    public class ChannelHealthTests
    {
        static ChannelHealth H(bool present = true, bool grabbing = true, long missed = 0,
                               long reductions = 1000, long grids = 1000, bool calibrated = true,
                               double luma = 65.0, double clip = 0.0, double black = 0.0)
            => ChannelHealthRule.Evaluate(present, grabbing, missed, reductions, grids,
                                          calibrated, luma, clip, black);

        [Fact]
        public void A_running_calibrated_channel_measuring_a_lit_panel_is_healthy()
        {
            Assert.Equal(ChannelHealth.Healthy, H());
        }

        [Fact]
        public void An_empty_port_and_a_stopped_camera_are_not_faults()
        {
            Assert.Equal(ChannelHealth.Absent, H(present: false));
            Assert.Equal(ChannelHealth.Stopped, H(grabbing: false));
            Assert.False(ChannelHealthRule.IsFault(ChannelHealth.Absent));
            Assert.False(ChannelHealthRule.IsFault(ChannelHealth.Stopped));
        }

        // ----- the two that events cannot show -----

        [Fact]
        public void A_lost_frame_is_a_fault()
        {
            Assert.Equal(ChannelHealth.FramesMissed, H(missed: 1));
            Assert.True(ChannelHealthRule.IsFault(ChannelHealth.FramesMissed));
        }

        /// <summary>
        /// Grids produced but not judged means frames going past unexamined - the failure that
        /// looks most like a quiet panel.
        /// </summary>
        [Fact]
        public void Grids_the_detector_did_not_judge_are_a_fault()
        {
            Assert.Equal(ChannelHealth.DetectorBlind, H(reductions: 1000, grids: 999));
            Assert.True(ChannelHealthRule.IsFault(ChannelHealth.DetectorBlind));
        }

        /// <summary>
        /// Losing frames outranks everything about the picture: the record is incomplete, which
        /// cannot be fixed by anything the optics say.
        /// </summary>
        [Fact]
        public void A_lost_frame_outranks_a_clipped_picture()
        {
            Assert.Equal(ChannelHealth.FramesMissed, H(missed: 1, clip: 50.0, calibrated: false));
        }

        [Fact]
        public void A_blind_detector_outranks_a_clipped_picture()
        {
            Assert.Equal(ChannelHealth.DetectorBlind, H(grids: 0, clip: 50.0, calibrated: false));
        }

        /// <summary>Before a run there are no reductions, and that is not blindness.</summary>
        [Fact]
        public void A_channel_that_has_reduced_nothing_yet_is_not_called_blind()
        {
            Assert.NotEqual(ChannelHealth.DetectorBlind, H(reductions: 0, grids: 0));
        }

        // ----- the picture -----

        [Fact]
        public void Clipping_or_crushing_puts_the_optics_out_of_band()
        {
            Assert.Equal(ChannelHealth.OpticsOutOfBand, H(clip: 1.5));
            Assert.Equal(ChannelHealth.OpticsOutOfBand, H(black: 1.5));
            Assert.Equal(ChannelHealth.OpticsOutOfBand, H(luma: 5.0));
            Assert.Equal(ChannelHealth.OpticsOutOfBand, H(luma: 250.0));
        }

        /// <summary>
        /// The dimmest of these three cameras runs at luma 53 against 65 and 66. It wants more
        /// light and it is not a fault, and a threshold invented between them would put an amber
        /// dot on a working rig.
        /// </summary>
        [Fact]
        public void The_dim_camera_on_this_rig_is_not_flagged()
        {
            Assert.Equal(ChannelHealth.Healthy, H(luma: 53.0));
        }

        // ----- calibration -----

        [Fact]
        public void A_threshold_that_was_never_measured_is_called_out()
        {
            Assert.Equal(ChannelHealth.Uncalibrated, H(calibrated: false));
            Assert.False(ChannelHealthRule.IsFault(ChannelHealth.Uncalibrated));
        }

        /// <summary>A clipped picture outranks an unmeasured threshold: it invalidates the reading.</summary>
        [Fact]
        public void Out_of_band_optics_outrank_an_unmeasured_threshold()
        {
            Assert.Equal(ChannelHealth.OpticsOutOfBand, H(calibrated: false, clip: 2.0));
        }

        [Fact]
        public void Every_state_has_something_to_say()
        {
            foreach (ChannelHealth h in System.Enum.GetValues(typeof(ChannelHealth)))
                Assert.False(string.IsNullOrWhiteSpace(ChannelHealthRule.Describe(h)), h.ToString());
        }
    }
}
