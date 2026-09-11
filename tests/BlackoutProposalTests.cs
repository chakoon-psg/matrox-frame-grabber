using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// What the proposal says when there was nothing to propose from.
    ///
    /// The first end-to-end test on hardware printed "panel floor 0.0 luma", which reads as a
    /// measurement and was not one: every frame of that run was inside a blackout, so no healthy
    /// frame was ever seen. A number that was never measured must not be printed as one.
    /// </summary>
    public class BlackoutProposalTests
    {
        [Fact]
        public void No_healthy_frame_means_no_floor_to_report()
        {
            var p = new BlackoutProposal(0.0, 0.0, 3609, 0.0, 0.0);

            Assert.False(p.SawHealthyFrame);
            Assert.False(p.IsUsable);
            Assert.Contains("no frame was outside a blackout", p.ToString());
            Assert.DoesNotContain("panel floor 0.0", p.ToString());
        }

        [Fact]
        public void A_measured_floor_is_reported_with_its_margin()
        {
            var p = new BlackoutProposal(36.9, 40.4, 500000, 18.45, 20.2);

            Assert.True(p.SawHealthyFrame);
            Assert.True(p.IsUsable);
            Assert.Contains("panel floor 36.9", p.ToString());
            // 18.45 formats as 18.4: F1 rounds the binary value, which is a hair under the decimal.
            Assert.Contains("enter 18.4", p.ToString());
        }

        [Fact]
        public void A_measured_floor_from_too_short_a_run_says_so()
        {
            var p = new BlackoutProposal(36.9, 40.4, 500, 18.45, 20.2);

            Assert.True(p.SawHealthyFrame);
            Assert.False(p.IsUsable);
            Assert.Contains("NOT ENOUGH", p.ToString());
        }
    }
}
