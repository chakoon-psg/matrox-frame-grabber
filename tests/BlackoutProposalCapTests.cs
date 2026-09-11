using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Why the proposal does not halve the floor.
    ///
    /// Halving is right for a depth, which is a fraction of a signal. This is an absolute level
    /// whose target reads near zero, and the 2026-09-11 calibration hour showed what halving
    /// gives: floors of 118.9 / 87.8 / 36.2 luma would have proposed 59.5 / 43.9 / 18.1 - and 59
    /// luma is not a panel that is gone, it is a panel at half brightness, which is what Dropout
    /// is for.
    /// </summary>
    public class BlackoutProposalCapTests
    {
        private const double Period = 1.0 / 120.0;

        private static TileGrid Structured(long frame, double median, double spread)
        {
            var g = new TileGrid { FrameNumber = frame };
            const int n = 64;
            for (int t = 0; t < TileGrid.TileCount; t++)
            {
                double v = t < TileGrid.TileCount / 2 ? median - spread / 2.0 : median + spread / 2.0;
                if (v < 0.0) v = 0.0;
                g.Accumulate(t, (long)(v * n), (long)(v * v * n), n);
            }
            return g;
        }

        private static BlackoutProposal Run(double floorLuma, double spreadThere)
        {
            var d = new BlackoutDetector(new BlackoutThresholds
            {
                EnterLuma = 6.0, ExitLuma = 12.0, MaxSpread = 4.0,
                EnterMs = 500.0, RecoverMs = 500.0,
            });
            for (int i = 0; i < 300; i++)
                d.Observe(Structured(i + 1, floorLuma, spreadThere), i * Period);
            return d.Propose();
        }

        /// <summary>
        /// Two of the three channels from the calibration hour. A bright channel must not be
        /// handed a high threshold just because its picture is bright - a panel that is off reads
        /// near zero on all of them.
        /// </summary>
        [Theory]
        [InlineData(118.9, 144.0)]
        [InlineData(87.8, 124.6)]
        public void A_bright_channel_is_capped_near_the_sensor_floor(double floor, double spread)
        {
            BlackoutProposal p = Run(floor, spread);

            Assert.Equal(floor, p.MinLuma, 1);
            Assert.Equal(BlackoutProposal.NearSensorFloor, p.SuggestedEnterLuma, 3);
            Assert.True(p.SuggestedEnterLuma < floor / 4.0,
                        "on a bright channel it is the cap that binds, not the quarter");
        }

        /// <summary>The darkest channel sits under the cap, so there the quarter binds.</summary>
        [Fact]
        public void A_dim_channel_gets_a_quarter_of_its_floor()
        {
            BlackoutProposal p = Run(36.2, 39.6);

            Assert.Equal(36.2 / 4.0, p.SuggestedEnterLuma, 2);
            Assert.True(p.SuggestedEnterLuma < BlackoutProposal.NearSensorFloor);
        }

        /// <summary>
        /// The number halving would have given, kept as the reason the cap exists.
        /// </summary>
        [Fact]
        public void Halving_would_have_called_a_dim_panel_a_gone_one()
        {
            BlackoutProposal p = Run(118.9, 144.0);

            Assert.InRange(p.MinLuma / 2.0, 59.0, 60.0);      // what halving gives
            Assert.True(p.SuggestedEnterLuma < 15.0, "and the proposal does not");
        }

        /// <summary>
        /// It bounds false positives and says nothing about catching a real fault. Nothing in an
        /// hour where nothing went black can say what a blackout reads.
        /// </summary>
        [Fact]
        public void A_proposal_is_only_safe_against_false_positives()
        {
            BlackoutProposal p = Run(118.9, 144.0);

            Assert.True(p.SuggestedEnterLuma < p.MinLuma,
                        "below the floor, so a working panel cannot trip it");
            Assert.True(p.SawHealthyFrame);
        }

        /// <summary>The flatness suggestion is bounded the same way, for the same reason.</summary>
        [Fact]
        public void The_flatness_suggestion_is_bounded_too()
        {
            BlackoutProposal p = Run(118.9, 144.0);

            Assert.Equal(144.0, p.MaxFlatSpreadAtDarkest, 1);
            Assert.Equal(BlackoutProposal.NearSensorFloor, p.SuggestedMaxSpread, 3);
        }
    }
}
