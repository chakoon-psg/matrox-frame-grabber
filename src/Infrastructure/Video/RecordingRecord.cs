using System;
using System.Globalization;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// What one recording was, as a record that travels with it.
    ///
    /// The video file alone cannot answer the questions this rig exists to answer. It cannot say
    /// which panel was in front of the camera, when in wall-clock terms its first frame was taken,
    /// or whether anything is missing from it - and for a reliability test that evidence is the
    /// product, not the pixels. So every recording gets a sidecar, and the sidecar is what a report
    /// cites.
    ///
    /// The one computed field is the drift, and it is the audit: **the rate a file declares should
    /// be the rate the camera delivered.** Both are known - the declared rate from the header, the
    /// delivered rate from the board's own per-frame timestamps - and they only disagree when
    /// something is wrong. Measured on this rig with nothing wrong: 124.3334 delivered against
    /// 124.316 declared, a systematic +0.014% from the camera's oscillator against the rate it
    /// reports. Anything larger is a defect, not a tolerance.
    ///
    /// <c>TestId</c> and <c>DutId</c> are deliberately allowed to be empty. They are not available
    /// yet, and a field that exists and is empty can be filled later without touching what has
    /// already been written - which is the point, because a record must not be reorganised after
    /// the fact.
    /// </summary>
    public sealed class RecordingRecord
    {
        // ----- identity -----

        /// <summary>The test programme this belongs to. Empty until there is one.</summary>
        public string TestId { get; set; } = string.Empty;

        /// <summary>The device under test. Empty until there is one; the camera name is the stand-in.</summary>
        public string DutId { get; set; } = string.Empty;

        /// <summary>The channel and its operator-given name, e.g. "PANEL-A123_ch0".</summary>
        public string Camera { get; set; } = string.Empty;

        // ----- when -----

        /// <summary>Wall clock at the start, ISO 8601 with the offset.</summary>
        public DateTimeOffset Started { get; set; }

        /// <summary>Wall clock at the stop.</summary>
        public DateTimeOffset Stopped { get; set; }

        /// <summary>
        /// The board's timestamp for the first and last frame of the run, and the span between.
        ///
        /// This is the good clock - the wall clock above is only accurate to the tick that read it,
        /// while these come from the board itself, two reads per grab.
        /// </summary>
        public double BoardFirstSec { get; set; }

        public double BoardLastSec { get; set; }

        // ----- what was acquired -----

        /// <summary>Frames the grab took, including any this recording did not want.</summary>
        public long AcquiredFrames { get; set; }

        /// <summary>
        /// The detector's frame number for the first frame in the file, and for the last. 0 when
        /// the recording does not know - a session made of segments has no single answer.
        ///
        /// **Without these a frame in an evidence file cannot be named.** The file is a plain
        /// sequence and the detector reports faults by frame number, so joining the two needs the
        /// offset, and the offset was only ever in the log. A log gets rotated; a record beside
        /// the evidence does not. The relation is
        ///
        ///     frame index in file = frame number - first_frame
        ///
        /// and it holds exactly when frames_dropped is 0, which is the usual case and is recorded
        /// right here beside it. When frames were lost to the ring the index shifts by however
        /// many were lost before it, so the two fields together say "this file is a contiguous
        /// run from first_frame" only if nothing was dropped.
        ///
        /// cs section 7.3 is the same defect one step worse: its camera_frame_id was off by one
        /// against the video it was meant to join, and nothing said so.
        /// </summary>
        public long FirstFrame { get; set; }

        public long LastFrame { get; set; }

        /// <summary>Frames actually written to the file.</summary>
        public long FramesWritten { get; set; }

        /// <summary>
        /// The rate the *acquisition* tier declares - the camera's reported ResultingFrameRate,
        /// which the file's rate is derived from.
        /// </summary>
        public double SourceDeclaredFps { get; set; }

        /// <summary>
        /// The rate this file's own header declares. Different from the source rate whenever the
        /// file takes every Nth frame: 124.316 at every 4th is 31.079.
        /// </summary>
        public double FileDeclaredFps { get; set; }

        /// <summary>Frames this file takes out of each N. 1 means every frame.</summary>
        public int EveryNthFrame { get; set; } = 1;

        // ----- what was lost, by layer -----

        /// <summary>Acquisition-side: the camera sent them and there was nowhere to put them.</summary>
        public long FramesMissed { get; set; }

        /// <summary>Encoder-side: the queue was full, so the frame was never read out.</summary>
        public long FramesSkipped { get; set; }

        /// <summary>Encoder-side: queued and then discarded.</summary>
        public long FramesDropped { get; set; }

        /// <summary>Turned away by design, because this file takes every Nth frame. Not a loss.</summary>
        public long FramesNotWanted { get; set; }

        // ----- how it was written -----

        public string Encoding { get; set; } = string.Empty;
        public string Container { get; set; } = string.Empty;
        public double SegmentSeconds { get; set; }
        public string Files { get; set; } = string.Empty;
        public string SegmentList { get; set; } = string.Empty;

        // ----- the audit -----

        /// <summary>Seconds of real time the run covered, from the board's own clock.</summary>
        public double BoardSpanSec => BoardLastSec > BoardFirstSec ? BoardLastSec - BoardFirstSec : 0.0;

        /// <summary>
        /// The rate the camera actually delivered: frames over the span between the first and the
        /// last, which is <c>frames - 1</c> periods rather than <c>frames</c>.
        /// </summary>
        public double DeliveredFps =>
            BoardSpanSec > 0.0 && AcquiredFrames > 1 ? (AcquiredFrames - 1) / BoardSpanSec : 0.0;

        /// <summary>
        /// How far the file's timeline is from real time, as a percentage. Positive means the file
        /// claims more time than passed, so it plays slow.
        ///
        /// Zero is the target and the only correct answer: a declared rate is not a preference. The
        /// residual on this rig is +0.014%, which is the camera's oscillator against the rate it
        /// reports; a loss shows up here too, in proportion, and the counters above say which layer
        /// it came from.
        ///
        /// Measured at the acquisition rather than at the file, and the two are the same number:
        /// the file's rate is the source's divided by an exact integer, so dividing both sides of
        /// the ratio by it changes nothing. Taking it at the acquisition avoids an off-by-a-few-
        /// frames error - the recording starts a moment after the grab, so its own first and last
        /// frames do not span the interval the board timestamps describe.
        /// </summary>
        public double DriftPercent =>
            DeliveredFps > 0.0 && SourceDeclaredFps > 0.0
                ? (DeliveredFps / SourceDeclaredFps - 1.0) * 100.0
                : 0.0;

        /// <summary>The drift as milliseconds per minute, which is easier to judge than a percent.</summary>
        public double DriftMsPerMinute => DriftPercent * 600.0;

        /// <summary>Every kind of loss added up. Zero is the acceptance criterion.</summary>
        public long FramesLost => FramesMissed + FramesSkipped + FramesDropped;

        /// <summary>
        /// Whether the timeline can be trusted: nothing lost, and the declared rate within a
        /// tenth of a percent of what was delivered.
        ///
        /// A tenth of a percent is seven times the measured systematic residual and about 0.6 s an
        /// hour - loose enough not to cry over the oscillator, tight enough that a single percent
        /// of loss fails it.
        ///
        /// A run with no measurable span is *not* trustworthy. Without two board timestamps there
        /// is nothing to check the header against, and reporting that as trustworthy would put a
        /// clean word on the one case where nothing was verified - which is the failure this whole
        /// record exists to prevent.
        /// </summary>
        public bool TimelineTrustworthy =>
            DeliveredFps > 0.0 && FramesLost == 0 && Math.Abs(DriftPercent) < 0.1;

        /// <summary>One line for the log, in the order somebody diagnosing would want it.</summary>
        public string Summary() =>
            string.Format(CultureInfo.InvariantCulture,
                "delivered {0:F4} fps ({1} frames over {2:F3} s board time) against declared "
              + "{3:F3}, drift {4:+0.000;-0.000}% ({5:+0;-0} ms/min); "
              + "file every {12} frame(s) at {13:F3} fps, "
              + "written {6}, missed {7}, skipped {8}, dropped {9}, not wanted {10}{11}",
                DeliveredFps, AcquiredFrames, BoardSpanSec, SourceDeclaredFps,
                DriftPercent, DriftMsPerMinute,
                FramesWritten, FramesMissed, FramesSkipped, FramesDropped, FramesNotWanted,
                TimelineTrustworthy ? "" : "  <-- TIMELINE NOT TRUSTWORTHY",
                EveryNthFrame, FileDeclaredFps);

        /// <summary>
        /// The record as JSON, hand-written rather than serialized.
        ///
        /// Hand-written because this file is read by whatever the lab already uses - a script, a
        /// spreadsheet, somebody's eyes - and the field order and formatting are part of what it
        /// says. Invariant culture throughout: a decimal comma in a record that crosses machines is
        /// a silent corruption.
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\n");
            Str(sb, "test_id", TestId);
            Str(sb, "dut_id", DutId);
            Str(sb, "camera", Camera);
            Str(sb, "started", Started.ToString("o", CultureInfo.InvariantCulture));
            Str(sb, "stopped", Stopped.ToString("o", CultureInfo.InvariantCulture));
            Num(sb, "board_first_s", BoardFirstSec, "F6");
            Num(sb, "board_last_s", BoardLastSec, "F6");
            Num(sb, "board_span_s", BoardSpanSec, "F6");
            Int(sb, "first_frame", FirstFrame);
            Int(sb, "last_frame", LastFrame);
            Int(sb, "acquired_frames", AcquiredFrames);
            Int(sb, "frames_written", FramesWritten);
            Num(sb, "source_delivered_fps", DeliveredFps, "F4");
            Num(sb, "source_declared_fps", SourceDeclaredFps, "F4");
            Num(sb, "file_declared_fps", FileDeclaredFps, "F4");
            Int(sb, "every_nth_frame", EveryNthFrame);
            Num(sb, "drift_percent", DriftPercent, "F4");
            Int(sb, "frames_missed", FramesMissed);
            Int(sb, "frames_skipped", FramesSkipped);
            Int(sb, "frames_dropped", FramesDropped);
            Int(sb, "frames_not_wanted", FramesNotWanted);
            Bool(sb, "timeline_trustworthy", TimelineTrustworthy);
            Str(sb, "encoding", Encoding);
            Str(sb, "container", Container);
            Num(sb, "segment_seconds", SegmentSeconds, "F1");
            Str(sb, "files", Files);
            Str(sb, "segment_list", SegmentList, last: true);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void Str(StringBuilder sb, string key, string value, bool last = false) =>
            sb.Append("  \"").Append(key).Append("\": \"").Append(Escape(value ?? string.Empty))
              .Append(last ? "\"\n" : "\",\n");

        private static void Num(StringBuilder sb, string key, double value, string format) =>
            sb.Append("  \"").Append(key).Append("\": ")
              .Append(value.ToString(format, CultureInfo.InvariantCulture)).Append(",\n");

        private static void Int(StringBuilder sb, string key, long value) =>
            sb.Append("  \"").Append(key).Append("\": ")
              .Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");

        private static void Bool(StringBuilder sb, string key, bool value) =>
            sb.Append("  \"").Append(key).Append("\": ").Append(value ? "true" : "false").Append(",\n");

        // Backslash and quote only. The values here are file names, camera names and ISO dates -
        // a control character would mean something upstream is already wrong.
        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
