using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The two fields that let a frame in an evidence file be named.
    ///
    /// Without them the file is a plain sequence and the detector reports by frame number, so the
    /// join needs an offset that only ever existed in the log. A log gets rotated; the record
    /// beside the evidence does not.
    /// </summary>
    public class RecordingRecordFramesTests
    {
        private static RecordingRecord Evidence() => new RecordingRecord
        {
            Camera = "Camera_0_ch0",
            FirstFrame = 4503,
            LastFrame = 5250,
            AcquiredFrames = 748,
            FramesWritten = 748,
            SourceDeclaredFps = 119.99,
            FileDeclaredFps = 119.99,
            BoardFirstSec = 1000.0,
            BoardLastSec = 1006.008,
        };

        [Fact]
        public void The_record_carries_both_ends_of_the_range()
        {
            string json = Evidence().ToJson();

            Assert.Contains("\"first_frame\": 4503", json);
            Assert.Contains("\"last_frame\": 5250", json);
        }

        /// <summary>
        /// The relation the fields exist for. 4503 is the number that was only in the log when
        /// frame 5000 of a real evidence file was verified against its PNG.
        /// </summary>
        [Fact]
        public void A_frame_number_maps_to_an_index_in_the_file()
        {
            RecordingRecord r = Evidence();

            Assert.Equal(497, 5000 - r.FirstFrame);
            Assert.Equal(0, r.FramesDropped);          // which is what makes the mapping exact
            Assert.Equal(748, r.LastFrame - r.FirstFrame + 1);
            Assert.Equal(r.FramesWritten, r.LastFrame - r.FirstFrame + 1);
        }

        /// <summary>A session made of segments has no single answer, and 0 says so.</summary>
        [Fact]
        public void A_recording_that_does_not_know_says_zero()
        {
            string json = new RecordingRecord { Camera = "c" }.ToJson();

            Assert.Contains("\"first_frame\": 0", json);
            Assert.Contains("\"last_frame\": 0", json);
        }

        /// <summary>
        /// When frames went missing the file is no longer a contiguous run, and the record says
        /// that in the column beside - which is the whole point of carrying both.
        /// </summary>
        [Fact]
        public void A_gap_shows_up_as_written_not_matching_the_range()
        {
            var r = new RecordingRecord
            {
                Camera = "c", FirstFrame = 100, LastFrame = 199,
                FramesWritten = 98, FramesDropped = 2, AcquiredFrames = 100,
            };

            Assert.Equal(100, r.LastFrame - r.FirstFrame + 1);
            Assert.NotEqual(r.FramesWritten, r.LastFrame - r.FirstFrame + 1);
            Assert.Equal(2, r.FramesLost);
        }
    }
}
