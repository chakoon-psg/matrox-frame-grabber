using System;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The record that travels with a recording, and the one sum in it that matters: the rate a
    /// file declares against the rate the camera delivered.
    ///
    /// That comparison is the whole audit. Both numbers are known - the header says one, the
    /// board's per-frame timestamps say the other - and they only disagree when something is wrong,
    /// so the arithmetic has to be right or the audit reports nonsense.
    /// </summary>
    public class RecordingRecordTests
    {
        /// <summary>A run measured on this rig 2026-09-11: 60 s, three channels, nothing lost.</summary>
        static RecordingRecord Measured() => new RecordingRecord
        {
            Camera = "Camera_0_ch0",
            BoardFirstSec = 100549.953274,
            BoardLastSec = 100610.073903,
            AcquiredFrames = 7476,
            FramesWritten = 7463,
            SourceDeclaredFps = 124.316, FileDeclaredFps = 31.079, EveryNthFrame = 4,
        };

        /// <summary>
        /// The span is between the first and last frame, so N frames cover N-1 periods. Dividing by
        /// N instead understates the rate by one frame's worth, which at 7476 frames is 0.013% -
        /// the same order as the residual being measured, so the off-by-one would hide it.
        /// </summary>
        [Fact]
        public void The_delivered_rate_counts_periods_not_frames()
        {
            RecordingRecord r = Measured();

            Assert.Equal(60.120629, r.BoardSpanSec, 6);
            Assert.Equal(124.3334, r.DeliveredFps, 4);
        }

        /// <summary>
        /// The residual this rig actually has: the camera's oscillator runs a shade faster than the
        /// rate it reports, so the file claims slightly more time than passed and plays slow.
        /// </summary>
        [Fact]
        public void The_measured_run_drifts_by_fourteen_thousandths_of_a_percent()
        {
            RecordingRecord r = Measured();

            Assert.Equal(0.0139, r.DriftPercent, 3);
            Assert.Equal(8.4, r.DriftMsPerMinute, 1);
            Assert.True(r.TimelineTrustworthy);
        }

        /// <summary>
        /// A loss shows up as drift in proportion, because the frames that are missing are the ones
        /// that would have filled the time. This is the case the check exists for.
        /// </summary>
        [Fact]
        public void A_lost_frame_moves_the_drift_and_fails_the_check()
        {
            // The full-resolution run that lost a third of its frames: 1023 taken of 1532 offered
            // over 12.3163 s, declared at the rate the camera claimed.
            var r = new RecordingRecord
            {
                BoardFirstSec = 76109.019361,
                BoardLastSec = 76121.335702,
                AcquiredFrames = 1023,
                FramesWritten = 985,
                SourceDeclaredFps = 124.285, FileDeclaredFps = 124.285,
                FramesMissed = 509,
            };

            Assert.Equal(83.0, r.DeliveredFps, 1);        // what really arrived
            Assert.True(r.DriftPercent < -30.0);          // the file plays a third too fast
            Assert.False(r.TimelineTrustworthy);
            Assert.Equal(509, r.FramesLost);
            Assert.Contains("NOT TRUSTWORTHY", r.Summary());
        }

        /// <summary>
        /// Frames a file did not want are not a loss. A session tier at every fourth frame turns
        /// three away by design, and counting those against the timeline would fail every run.
        /// </summary>
        [Fact]
        public void Frames_turned_away_by_design_are_not_counted_as_lost()
        {
            RecordingRecord r = Measured();
            r.FramesNotWanted = 5597;      // three of every four, at 31 fps out of 124

            Assert.Equal(0, r.FramesLost);
            Assert.True(r.TimelineTrustworthy);
        }

        [Fact]
        public void Every_layer_of_loss_counts_against_the_timeline()
        {
            RecordingRecord r = Measured();
            Assert.True(r.TimelineTrustworthy);

            r.FramesSkipped = 1;
            Assert.False(r.TimelineTrustworthy);

            r.FramesSkipped = 0; r.FramesDropped = 1;
            Assert.False(r.TimelineTrustworthy);

            r.FramesDropped = 0; r.FramesMissed = 1;
            Assert.False(r.TimelineTrustworthy);
        }

        /// <summary>
        /// The tolerance is seven times the measured residual and about 0.6 s an hour: loose enough
        /// to ignore the oscillator, tight enough that a percent of loss fails.
        /// </summary>
        [Fact]
        public void The_tolerance_admits_the_oscillator_and_refuses_a_percent()
        {
            var r = new RecordingRecord
            {
                BoardFirstSec = 0.0, BoardLastSec = 100.0, AcquiredFrames = 10001,
                SourceDeclaredFps = 100.0,
            };
            Assert.Equal(100.0, r.DeliveredFps, 6);
            Assert.Equal(0.0, r.DriftPercent, 6);
            Assert.True(r.TimelineTrustworthy);

            r.SourceDeclaredFps = 100.05;                       // 0.05% out
            Assert.True(r.TimelineTrustworthy);

            r.SourceDeclaredFps = 101.0;                        // 1% out
            Assert.False(r.TimelineTrustworthy);
        }

        /// <summary>
        /// And a run with nothing to check is not "fine". Recording while the grab is stopped, or a
        /// board clock that never advanced, leaves no second timestamp to compare the header
        /// against - and calling that trustworthy would put a clean word on the one case where
        /// nothing was verified.
        /// </summary>
        [Fact]
        public void A_run_with_no_span_reports_no_rate_and_is_not_trustworthy()
        {
            var r = new RecordingRecord { AcquiredFrames = 1, SourceDeclaredFps = 124.316 };

            Assert.Equal(0.0, r.BoardSpanSec, 6);
            Assert.Equal(0.0, r.DeliveredFps, 6);
            Assert.Equal(0.0, r.DriftPercent, 6);
            Assert.False(r.TimelineTrustworthy);
            Assert.Contains("NOT TRUSTWORTHY", r.Summary());
        }

        // ----- the file it writes -----

        /// <summary>
        /// The two identity fields are allowed to be empty and must still be present. A field that
        /// exists and is empty can be filled later; a field that is absent means every record
        /// written before it appeared has to be reorganised, which a controlled record may not be.
        /// </summary>
        [Fact]
        public void The_identity_fields_are_written_even_when_nobody_has_supplied_them()
        {
            string json = Measured().ToJson();

            Assert.Contains("\"test_id\": \"\"", json);
            Assert.Contains("\"dut_id\": \"\"", json);
            Assert.Contains("\"camera\": \"Camera_0_ch0\"", json);
        }

        /// <summary>
        /// Invariant culture throughout. A decimal comma in a record that crosses machines is a
        /// silent corruption, and this app runs on a Korean-locale Windows.
        /// </summary>
        [Fact]
        public void Numbers_are_written_with_a_decimal_point_whatever_the_locale()
        {
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");   // a comma locale
                string json = Measured().ToJson();

                Assert.Contains("\"source_declared_fps\": 124.3160", json);
                Assert.DoesNotContain("124,3160", json);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = culture;
            }
        }

        [Fact]
        public void A_windows_path_does_not_break_the_json()
        {
            var r = Measured();
            r.Files = @"C:\Users\user\Videos\Cam_0_%05d.ts";

            string json = r.ToJson();
            Assert.Contains(@"C:\\Users\\user\\Videos\\Cam_0_%05d.ts", json);
        }

        [Fact]
        public void The_json_is_one_object_with_every_field()
        {
            string json = Measured().ToJson();

            Assert.StartsWith("{", json);
            Assert.EndsWith("}\n", json);
            foreach (string key in new[]
            {
                "test_id", "dut_id", "camera", "started", "stopped", "board_first_s", "board_last_s",
                "board_span_s", "acquired_frames", "frames_written", "source_delivered_fps", "source_declared_fps",
                "file_declared_fps", "every_nth_frame", "drift_percent", "frames_missed", "frames_skipped", "frames_dropped",
                "frames_not_wanted", "timeline_trustworthy", "encoding", "container",
                "segment_seconds", "files", "segment_list",
            })
                Assert.Contains("\"" + key + "\":", json);
        }
    }
}
