using System;
using System.Collections.Generic;
using System.IO;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The rolling segment index: which files hold a window of board time, and which may be
    /// deleted. Both halves have a way of being quietly wrong - a window that silently starts late,
    /// and a file deleted while a clip is still reading it.
    /// </summary>
    public class SegmentRingTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _list;

        public SegmentRingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "segring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _list = Path.Combine(_dir, "seg.csv");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// Writes segment-list lines the way ffmpeg does, with the real boundaries measured on the
        /// hardware - 1.994 to 2.019 s against a nominal 2, which is why the list is parsed rather
        /// than the spans being derived from the file names.
        /// </summary>
        private void WriteList(int count, double nominal = 2.0)
        {
            var lines = new List<string>();
            double t = 0.0;
            for (int i = 0; i < count; i++)
            {
                double len = nominal + (i % 3 == 0 ? 0.019 : (i % 3 == 1 ? 0.003 : -0.005));
                string name = $"seg_{i:00000}.mp4";
                File.WriteAllText(Path.Combine(_dir, name), "x");
                lines.Add($"{name},{t.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}," +
                          $"{(t + len).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}");
                t += len;
            }
            File.WriteAllLines(_list, lines);
        }

        // ----- reading the list -----

        [Fact]
        public void Polling_takes_the_lines_the_muxer_has_written()
        {
            WriteList(3);
            var ring = new SegmentRing(_list, retentionSec: 60.0);
            Assert.Equal(3, ring.Poll());
            Assert.Equal(3, ring.Count);
        }

        /// <summary>Called on every tick, so it must only take what is new.</summary>
        [Fact]
        public void Polling_again_takes_only_the_new_lines()
        {
            WriteList(3);
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            WriteList(5);
            Assert.Equal(2, ring.Poll());
            Assert.Equal(5, ring.Count);
        }

        [Fact]
        public void A_missing_list_is_not_an_error()
        {
            var ring = new SegmentRing(Path.Combine(_dir, "nothing.csv"), 60.0);
            Assert.Equal(0, ring.Poll());
            Assert.Equal(0, ring.Count);
        }

        /// <summary>A partial write must not stop a recording.</summary>
        [Fact]
        public void A_line_that_does_not_parse_is_skipped()
        {
            File.WriteAllLines(_list, new[] { "seg_00000.mp4,0.000000,2.019000", "seg_00001.mp4,partial" });
            var ring = new SegmentRing(_list, 60.0);
            Assert.Equal(1, ring.Poll());
        }

        // ----- board time in, recording time out -----

        [Fact]
        public void Nothing_can_be_located_before_the_anchor_is_set()
        {
            WriteList(5);
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            Assert.False(ring.Cover(1.0, 3.0).Any);
        }

        [Fact]
        public void A_board_time_becomes_a_position_in_the_recording()
        {
            var ring = new SegmentRing(_list, 60.0);
            ring.Anchor(70000.0);   // board uptime when the encoder started
            Assert.Equal(0.0, ring.ToRecordingSec(70000.0), 6);
            Assert.Equal(12.5, ring.ToRecordingSec(70012.5), 6);
        }

        // ----- coverage -----

        [Fact]
        public void A_window_names_the_files_that_hold_it_and_where_it_starts()
        {
            WriteList(8);                        // roughly 0 .. 16 s
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            ring.Anchor(1000.0);

            // 5 s .. 9 s of the recording.
            SegmentCoverage c = ring.Cover(1005.0, 1009.0);
            Assert.True(c.Any);
            Assert.True(c.Files.Count >= 2, $"expected several files, got {c.Files.Count}");
            Assert.Equal(4.0, c.LengthSec, 3);
            Assert.True(c.OffsetSec >= 0.0 && c.OffsetSec < c.Files[0].LengthSec);
            Assert.False(c.ClippedAtStart);
            Assert.False(c.ClippedAtEnd);
        }

        /// <summary>
        /// A clip missing its lead-up is still evidence; one that silently starts late is not - so
        /// the shortfall is reported.
        /// </summary>
        [Fact]
        public void A_window_older_than_what_is_held_is_reported_as_clipped()
        {
            WriteList(4);
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            ring.Anchor(1000.0);

            SegmentCoverage c = ring.Cover(995.0, 1003.0);   // starts 5 s before the recording
            Assert.True(c.Any);
            Assert.True(c.ClippedAtStart);
            Assert.Equal(0.0, c.OffsetSec, 6);
        }

        [Fact]
        public void A_window_past_the_newest_closed_file_is_reported_as_clipped()
        {
            WriteList(2);                        // about 0 .. 4 s
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            ring.Anchor(1000.0);

            SegmentCoverage c = ring.Cover(1002.0, 1030.0);
            Assert.True(c.Any);
            Assert.True(c.ClippedAtEnd);
        }

        [Fact]
        public void A_window_entirely_outside_what_is_held_covers_nothing()
        {
            WriteList(3);
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            ring.Anchor(1000.0);
            Assert.False(ring.Cover(1100.0, 1110.0).Any);
        }

        // ----- retention -----

        [Fact]
        public void Segments_older_than_the_retention_are_deleted()
        {
            WriteList(20);                       // about 40 s
            var ring = new SegmentRing(_list, retentionSec: 10.0);
            ring.Poll();
            ring.Anchor(1000.0);

            int removed = ring.Trim(keepFromBoardSec: double.MaxValue);
            Assert.True(removed > 0);
            Assert.True(ring.OldestSec >= ring.NewestSec - 10.0 - 2.1);
            Assert.Equal(removed, ring.Deleted);
        }

        [Fact]
        public void Deleted_segments_are_gone_from_disk()
        {
            WriteList(20);
            var ring = new SegmentRing(_list, 4.0);
            ring.Poll();
            ring.Anchor(1000.0);
            string oldest = ring.Entries[0].Path;

            ring.Trim(double.MaxValue);
            Assert.False(File.Exists(oldest));
        }

        /// <summary>
        /// The reservation. Cutting reads the segments from the start of the first one - ffmpeg's
        /// concat demuxer cannot seek - so deleting a file a pending clip still needs breaks it
        /// mid-read.
        /// </summary>
        [Fact]
        public void A_segment_a_pending_clip_still_needs_is_not_deleted()
        {
            WriteList(20);
            var ring = new SegmentRing(_list, retentionSec: 4.0);
            ring.Poll();
            ring.Anchor(1000.0);

            // A clip pending from 6 s into the recording holds everything from there on.
            ring.Trim(keepFromBoardSec: 1006.0);
            Assert.True(ring.OldestSec <= 6.0, $"oldest was {ring.OldestSec}");
            Assert.True(File.Exists(ring.Entries[0].Path));
        }

        [Fact]
        public void No_retention_means_nothing_is_deleted()
        {
            WriteList(20);
            var ring = new SegmentRing(_list, retentionSec: 0.0);
            ring.Poll();
            ring.Anchor(1000.0);
            Assert.Equal(0, ring.Trim(double.MaxValue));
            Assert.Equal(20, ring.Count);
        }

        [Fact]
        public void Finishing_a_run_deletes_what_is_left()
        {
            WriteList(6);
            var ring = new SegmentRing(_list, 60.0);
            ring.Poll();
            string first = ring.Entries[0].Path;

            Assert.Equal(6, ring.DeleteAll());
            Assert.Equal(0, ring.Count);
            Assert.False(File.Exists(first));
        }
    }
}
