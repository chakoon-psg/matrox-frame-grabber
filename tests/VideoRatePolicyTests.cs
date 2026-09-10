using System;
using System.Globalization;
using System.Threading;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The rate arithmetic behind a recording's time axis. These exist because a 120.0 s run at
    /// 124.316 fps produced a file that read as 81.07 s at 184.06 fps with every frame present -
    /// the rate was taken from what the camera had been asked for, not what it can deliver.
    /// </summary>
    public class VideoRatePolicyTests
    {
        // ----- which rate a file declares -----

        /// <summary>
        /// The defect itself. At recording start the measured rate does not exist yet, and the
        /// configured AcquisitionFrameRate answers 184 for a camera the exposure limits to 124.316.
        /// The camera's own ResultingFrameRate has to win.
        /// </summary>
        [Fact]
        public void Declared_prefers_what_the_camera_can_deliver_over_what_it_was_asked_for()
        {
            Assert.Equal(124.316, VideoRatePolicy.Declared(124.316, 0.0, 184.0));
        }

        [Fact]
        public void Declared_falls_back_to_the_measured_rate_when_the_camera_does_not_answer()
        {
            Assert.Equal(124.3, VideoRatePolicy.Declared(0.0, 124.3, 184.0));
        }

        [Fact]
        public void Declared_uses_the_configured_rate_only_when_nothing_else_answered()
        {
            Assert.Equal(184.0, VideoRatePolicy.Declared(0.0, 0.0, 184.0));
        }

        [Fact]
        public void Declared_never_returns_an_unusable_rate()
        {
            Assert.Equal(VideoRatePolicy.FallbackFps, VideoRatePolicy.Declared(0.0, 0.0, 0.0));
            Assert.Equal(VideoRatePolicy.FallbackFps, VideoRatePolicy.Declared(-1.0, 0.5, double.NaN));
            Assert.Equal(VideoRatePolicy.FallbackFps,
                         VideoRatePolicy.Declared(double.NaN, double.PositiveInfinity, 0.0));
        }

        // ----- the rate of a file that skips frames -----

        /// <summary>
        /// "30 fps" is a request, not a header value. Every fourth frame of 124.316 fps is 31.079,
        /// and declaring 30 would make the file 3.6% slow - the same class of error as the defect
        /// above, arrived at from the other direction.
        /// </summary>
        [Fact]
        public void FileFps_divides_the_source_rate_and_does_not_round_to_the_wanted_one()
        {
            double fps = VideoRatePolicy.FileFps(124.316, 4);
            Assert.Equal(31.079, fps, 3);
            Assert.NotEqual(30.0, fps);
        }

        [Fact]
        public void FileFps_of_every_frame_is_the_source_rate()
        {
            Assert.Equal(124.316, VideoRatePolicy.FileFps(124.316, 1));
        }

        [Fact]
        public void FileFps_rejects_a_nonsense_divisor_or_source()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => VideoRatePolicy.FileFps(124.316, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => VideoRatePolicy.FileFps(0.0, 4));
        }

        [Fact]
        public void EveryNthFor_picks_the_divisor_closest_to_the_wanted_rate()
        {
            Assert.Equal(4, VideoRatePolicy.EveryNthFor(124.316, 30.0));
            Assert.Equal(2, VideoRatePolicy.EveryNthFor(124.316, 60.0));
            Assert.Equal(1, VideoRatePolicy.EveryNthFor(124.316, 124.316));
        }

        [Fact]
        public void EveryNthFor_takes_every_frame_when_more_is_wanted_than_the_source_has()
        {
            Assert.Equal(1, VideoRatePolicy.EveryNthFor(124.316, 200.0));
        }

        [Fact]
        public void ShouldFeed_takes_every_nth_frame_and_all_of_them_at_one()
        {
            Assert.True(VideoRatePolicy.ShouldFeed(0, 4));
            Assert.False(VideoRatePolicy.ShouldFeed(1, 4));
            Assert.False(VideoRatePolicy.ShouldFeed(3, 4));
            Assert.True(VideoRatePolicy.ShouldFeed(4, 4));

            for (long f = 0; f < 5; f++)
                Assert.True(VideoRatePolicy.ShouldFeed(f, 1));
        }

        // ----- keyframes -----

        [Fact]
        public void KeyframeInterval_is_the_wanted_seconds_in_frames()
        {
            Assert.Equal(62, VideoRatePolicy.KeyframeInterval(124.316, 0.5));
            Assert.Equal(124, VideoRatePolicy.KeyframeInterval(124.316, 1.0));
        }

        /// <summary>Zero seconds means "leave x264's default", not "a keyframe every frame".</summary>
        [Fact]
        public void KeyframeInterval_of_no_wanted_interval_is_zero()
        {
            Assert.Equal(0, VideoRatePolicy.KeyframeInterval(124.316, 0.0));
        }

        [Fact]
        public void KeyframeInterval_is_never_zero_for_an_interval_shorter_than_a_frame()
        {
            Assert.Equal(1, VideoRatePolicy.KeyframeInterval(124.316, 0.001));
        }

        // ----- formatting -----

        /// <summary>
        /// ffmpeg parses "124.316" to 31079/250 exactly. On a machine whose culture writes a comma
        /// for the decimal point - this app runs on Korean and German Windows - a culture-sensitive
        /// format would hand ffmpeg "124,316" and it would read something else entirely.
        /// </summary>
        [Fact]
        public void Format_writes_a_dot_whatever_the_machine_culture_is()
        {
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("124.316", VideoRatePolicy.Format(124.316));

                Thread.CurrentThread.CurrentCulture = new CultureInfo("ko-KR");
                Assert.Equal("31.079", VideoRatePolicy.Format(31.079));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }

        [Fact]
        public void Usable_rejects_zero_negative_and_not_a_number()
        {
            Assert.True(VideoRatePolicy.Usable(124.316));
            Assert.False(VideoRatePolicy.Usable(0.0));
            Assert.False(VideoRatePolicy.Usable(-30.0));
            Assert.False(VideoRatePolicy.Usable(double.NaN));
            Assert.False(VideoRatePolicy.Usable(double.PositiveInfinity));
        }
    }
}
