using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// Which backend records, and the reason shown for it. These exist because the first version of
    /// this decision collapsed two facts - whether a MIL sink exists and whether MIL accepts a
    /// context here - into one boolean, which would have made a sink delivered by the board's
    /// supplier impossible to run on this machine at all.
    /// </summary>
    public class VideoSinkPolicyTests
    {
        // ----- the gap this was written to close -----

        /// <summary>
        /// The whole point. A delivered implementation on a machine whose MIL refuses a context is
        /// still selectable, because that is the state it has to be tested in.
        /// </summary>
        [Fact]
        public void An_implemented_but_untested_mil_sink_can_still_be_chosen_explicitly()
        {
            SinkChoice c = VideoSinkPolicy.Choose(VideoSinkPreference.Mil, MilReadiness.Untested,
                                                  ffmpegFound: true, out string reason);
            Assert.Equal(SinkChoice.Mil, c);
            Assert.Contains("untested", reason);
        }

        /// <summary>
        /// And it is never picked for anybody. An operator must not land on an unproven encoder
        /// because a licence appeared overnight.
        /// </summary>
        [Fact]
        public void Auto_never_picks_an_untested_mil_sink()
        {
            SinkChoice c = VideoSinkPolicy.Choose(VideoSinkPreference.Auto, MilReadiness.Untested,
                                                  ffmpegFound: true, out string reason);
            Assert.Equal(SinkChoice.Ffmpeg, c);
            Assert.Equal("ffmpeg", reason);
        }

        /// <summary>
        /// Falling back would make a broken MIL sink look like it works, which is the one outcome a
        /// test must not produce.
        /// </summary>
        [Fact]
        public void An_explicit_mil_choice_does_not_fall_back_to_ffmpeg()
        {
            SinkChoice c = VideoSinkPolicy.Choose(VideoSinkPreference.Mil, MilReadiness.Untested,
                                                  ffmpegFound: true, out _);
            Assert.NotEqual(SinkChoice.Ffmpeg, c);
        }

        // ----- not implemented -----

        [Fact]
        public void An_unimplemented_mil_sink_is_not_selectable()
        {
            Assert.False(VideoSinkPolicy.MilSelectable(MilReadiness.NotImplemented));
            Assert.True(VideoSinkPolicy.MilSelectable(MilReadiness.Untested));
            Assert.True(VideoSinkPolicy.MilSelectable(MilReadiness.Ready));
        }

        [Fact]
        public void Choosing_an_unimplemented_mil_sink_records_nothing_and_says_why()
        {
            SinkChoice c = VideoSinkPolicy.Choose(VideoSinkPreference.Mil, MilReadiness.NotImplemented,
                                                  ffmpegFound: true, out string reason);
            Assert.Equal(SinkChoice.None, c);
            Assert.Contains("not implemented", reason);
        }

        // ----- ready -----

        [Fact]
        public void Auto_prefers_a_ready_mil_sink_over_ffmpeg()
        {
            SinkChoice c = VideoSinkPolicy.Choose(VideoSinkPreference.Auto, MilReadiness.Ready,
                                                  ffmpegFound: true, out string reason);
            Assert.Equal(SinkChoice.Mil, c);
            Assert.Equal("MIL/Mseq", reason);
        }

        [Fact]
        public void A_ready_mil_sink_is_named_without_a_caveat()
        {
            VideoSinkPolicy.Choose(VideoSinkPreference.Mil, MilReadiness.Ready, true, out string reason);
            Assert.Equal("MIL/Mseq", reason);
        }

        // ----- ffmpeg -----

        [Fact]
        public void Auto_falls_through_to_ffmpeg_when_mil_is_not_ready()
        {
            foreach (MilReadiness mil in new[] { MilReadiness.NotImplemented, MilReadiness.Untested })
                Assert.Equal(SinkChoice.Ffmpeg,
                    VideoSinkPolicy.Choose(VideoSinkPreference.Auto, mil, true, out _));
        }

        [Fact]
        public void An_explicit_ffmpeg_choice_ignores_a_ready_mil_sink()
        {
            Assert.Equal(SinkChoice.Ffmpeg,
                VideoSinkPolicy.Choose(VideoSinkPreference.Ffmpeg, MilReadiness.Ready, true, out _));
        }

        [Fact]
        public void Nothing_records_when_ffmpeg_is_missing_and_mil_is_not_ready()
        {
            SinkChoice c = VideoSinkPolicy.Choose(VideoSinkPreference.Auto, MilReadiness.NotImplemented,
                                                  ffmpegFound: false, out string reason);
            Assert.Equal(SinkChoice.None, c);
            Assert.Contains("ffmpeg was not found", reason);
        }

        /// <summary>
        /// With no ffmpeg and an untested sink sitting there, the reason has to say that trying it
        /// is an option - otherwise the only readable message is about ffmpeg and the way forward
        /// is invisible.
        /// </summary>
        [Fact]
        public void With_no_ffmpeg_the_reason_points_at_the_untested_sink()
        {
            VideoSinkPolicy.Choose(VideoSinkPreference.Auto, MilReadiness.Untested,
                                   ffmpegFound: false, out string reason);
            Assert.Contains("select it explicitly", reason);
        }
    }
}
