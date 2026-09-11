using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The gate in front of the evidence ring. It exists because Windows does not refuse an
    /// allocation that does not fit - it pages for it, and paging beside the acquisition is the
    /// load measured to cost frames.
    /// </summary>
    public class HostMemoryTests
    {
        private const long Gb = 1000L * 1000 * 1000;

        [Fact]
        public void A_ring_that_fits_in_the_share_goes_ahead()
        {
            Assert.True(HostMemory.Fits(8 * Gb, available: 19 * Gb));    // three channels, +-2 s
        }

        [Fact]
        public void A_ring_bigger_than_the_share_is_refused()
        {
            // Not "bigger than free": the display, the encoders and the grab rings still have to
            // fit, so a ring that would take nearly all of it is already too big.
            Assert.False(HostMemory.Fits(16 * Gb, available: 19 * Gb));
        }

        /// <summary>
        /// A failed query returns 0, and refusing on that would turn a diagnostic into an outage -
        /// the feature would silently switch itself off on any machine the call did not work on.
        /// </summary>
        [Fact]
        public void An_unreadable_machine_is_allowed_through()
        {
            Assert.True(HostMemory.Fits(8 * Gb, available: 0L));
        }

        [Fact]
        public void Asking_for_nothing_always_fits()
        {
            Assert.True(HostMemory.Fits(0L, available: 1L));
            Assert.True(HostMemory.Fits(-1L, available: 0L));
        }

        [Fact]
        public void The_share_is_adjustable()
        {
            Assert.True(HostMemory.Fits(9 * Gb, 10 * Gb, share: 0.95));
            Assert.False(HostMemory.Fits(9 * Gb, 10 * Gb, share: 0.5));
        }

        /// <summary>Not a fixed number, but a real machine reports one.</summary>
        [Fact]
        public void The_machine_reports_what_is_free()
        {
            Assert.True(HostMemory.AvailableBytes() > 0L);
        }
    }
}
