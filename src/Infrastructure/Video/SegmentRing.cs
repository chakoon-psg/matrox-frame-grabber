using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>One closed segment file and the span of the recording it holds.</summary>
    public readonly struct SegmentEntry
    {
        public readonly string Path;

        /// <summary>Seconds from the start of the recording, as ffmpeg's segment list reports them.</summary>
        public readonly double FromSec;

        public readonly double ToSec;

        public SegmentEntry(string path, double fromSec, double toSec)
        {
            Path = path;
            FromSec = fromSec;
            ToSec = toSec;
        }

        public double LengthSec => ToSec - FromSec;
    }

    /// <summary>The files covering a window, and where in the first one it starts.</summary>
    public readonly struct SegmentCoverage
    {
        /// <summary>Files in order. Empty when the window is not held.</summary>
        public readonly IReadOnlyList<SegmentEntry> Files;

        /// <summary>Seconds into <see cref="Files"/>[0] where the window begins.</summary>
        public readonly double OffsetSec;

        /// <summary>Seconds to take from that offset.</summary>
        public readonly double LengthSec;

        /// <summary>
        /// True when the window began before the oldest file still held, so the clip will be short
        /// at the front. Reported rather than hidden: a clip missing its lead-up is still evidence,
        /// and one that silently starts late is not.
        /// </summary>
        public readonly bool ClippedAtStart;

        /// <summary>True when the window runs past the newest closed file.</summary>
        public readonly bool ClippedAtEnd;

        public SegmentCoverage(IReadOnlyList<SegmentEntry> files, double offsetSec, double lengthSec,
                               bool clippedAtStart, bool clippedAtEnd)
        {
            Files = files;
            OffsetSec = offsetSec;
            LengthSec = lengthSec;
            ClippedAtStart = clippedAtStart;
            ClippedAtEnd = clippedAtEnd;
        }

        public bool Any => Files != null && Files.Count > 0;
    }

    /// <summary>
    /// The rolling set of segment files a recording leaves behind, and which of them hold a given
    /// window of board time.
    ///
    /// ffmpeg's segment muxer writes one line per file as it closes it - filename, start, end - and
    /// that list is the truth about the boundaries: measured, the files came out 1.994 to 2.019 s
    /// long against a nominal 2, so deriving the spans from the file names would put every window
    /// a little off. Reading the list also tells this class which files are closed, and a file the
    /// muxer is still writing cannot be read.
    ///
    /// Board time comes in and recording time goes out. Anomalies are timestamped by the board -
    /// the only clock every channel shares - while the segment list counts from the moment the
    /// encoder started, so one anchor pair converts between them.
    ///
    /// MIL-free: the parsing, the coverage and the retention are arithmetic and file names.
    /// </summary>
    public sealed class SegmentRing
    {
        private readonly List<SegmentEntry> _entries = new List<SegmentEntry>();
        private readonly string _listPath;
        private long _consumedLines;

        /// <summary>
        /// The board stamp of the first frame the encoder received, so a board time can be turned
        /// into a position in the recording.
        /// </summary>
        public double AnchorBoardSec { get; private set; }

        /// <summary>Whether the anchor has been set. Until it is, nothing can be located.</summary>
        public bool Anchored { get; private set; }

        /// <summary>Seconds of recording to keep. Older closed files are deleted.</summary>
        public double RetentionSec { get; set; }

        /// <summary>Files deleted, for the summary.</summary>
        public long Deleted { get; private set; }

        /// <summary>
        /// Polls that could not read the list. Non-zero for a whole run means the ring never
        /// learned its own files, and every clip will have failed for a reason that looks like
        /// "the window is too old".
        /// </summary>
        public long PollFailures { get; private set; }

        /// <summary>Why the last poll failed, if one did.</summary>
        public string LastPollError { get; private set; }

        /// <summary>Segments the list has announced.</summary>
        public int Count => _entries.Count;

        /// <summary>The newest closed segment's end, in recording seconds. Zero when none.</summary>
        public double NewestSec => _entries.Count == 0 ? 0.0 : _entries[_entries.Count - 1].ToSec;

        /// <summary>The oldest held segment's start, in recording seconds.</summary>
        public double OldestSec => _entries.Count == 0 ? 0.0 : _entries[0].FromSec;

        public SegmentRing(string segmentListPath, double retentionSec)
        {
            _listPath = segmentListPath;
            RetentionSec = retentionSec < 0.0 ? 0.0 : retentionSec;
        }

        /// <summary>
        /// Sets the board time the recording started at. Called once, with the stamp of the first
        /// frame actually fed to the encoder.
        /// </summary>
        public void Anchor(double boardSec)
        {
            if (Anchored) return;
            AnchorBoardSec = boardSec;
            Anchored = true;
        }

        /// <summary>Recording seconds for a board time. Only meaningful once anchored.</summary>
        public double ToRecordingSec(double boardSec) => boardSec - AnchorBoardSec;

        /// <summary>
        /// Reads any new lines from the segment list. Cheap and safe to call on every stats tick:
        /// the file only grows, and lines already taken are skipped.
        /// </summary>
        public int Poll()
        {
            if (string.IsNullOrEmpty(_listPath)) return 0;

            var lines = new List<string>();
            try
            {
                if (!File.Exists(_listPath)) return 0;

                // FileShare.ReadWrite because the muxer holds this file open and keeps appending:
                // the default share mode refuses, and File.ReadAllLines therefore threw on every
                // poll for a whole run while the list filled up beside it.
                using (var fs = new FileStream(_listPath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                }
            }
            catch (IOException e)
            {
                // Counted, not swallowed. A read that fails once is a partial write; one that fails
                // every time is a ring that will never know its own files, and the clip failure it
                // causes says nothing about the cause.
                PollFailures++;
                LastPollError = e.Message;
                return 0;
            }
            catch (UnauthorizedAccessException e)
            {
                PollFailures++;
                LastPollError = e.Message;
                return 0;
            }

            int added = 0;
            for (long i = _consumedLines; i < lines.Count; i++)
            {
                if (TryParse(lines[(int)i], out SegmentEntry e))
                {
                    _entries.Add(e);
                    added++;
                }
                _consumedLines = i + 1;
            }
            return added;
        }

        /// <summary>
        /// ffmpeg writes "name,start,end" with an invariant decimal point. A line that does not
        /// parse is skipped rather than fatal: it is either a partial write or a format this build
        /// does not produce, and neither should stop a recording.
        /// </summary>
        private bool TryParse(string line, out SegmentEntry entry)
        {
            entry = default;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string[] parts = line.Split(',');
            if (parts.Length < 3) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double from)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double to)) return false;

            // The list holds bare file names; the files sit beside the list.
            string dir = Path.GetDirectoryName(_listPath) ?? string.Empty;
            string name = parts[0].Trim();
            string full = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
            entry = new SegmentEntry(full, from, to);
            return true;
        }

        /// <summary>
        /// The files covering a board-time window, in order.
        ///
        /// Only closed segments are considered: a file the muxer is still writing is not readable,
        /// which is why a clip has a due time rather than being cut when the event is reported.
        /// </summary>
        public SegmentCoverage Cover(double fromBoardSec, double toBoardSec)
        {
            var none = new SegmentCoverage(Array.Empty<SegmentEntry>(), 0.0, 0.0, false, false);
            if (!Anchored || _entries.Count == 0 || toBoardSec <= fromBoardSec) return none;

            double from = ToRecordingSec(fromBoardSec);
            double to = ToRecordingSec(toBoardSec);

            var hit = new List<SegmentEntry>();
            foreach (SegmentEntry e in _entries)
                if (e.ToSec > from && e.FromSec < to)
                    hit.Add(e);

            if (hit.Count == 0) return none;

            bool clippedStart = from < hit[0].FromSec;
            bool clippedEnd = to > hit[hit.Count - 1].ToSec;

            double start = Math.Max(from, hit[0].FromSec);
            double end = Math.Min(to, hit[hit.Count - 1].ToSec);
            return new SegmentCoverage(hit, start - hit[0].FromSec, Math.Max(0.0, end - start),
                                       clippedStart, clippedEnd);
        }

        /// <summary>
        /// Deletes closed segments older than the retention, never touching one at or after
        /// <paramref name="keepFromBoardSec"/>.
        ///
        /// That reservation is not optional. Cutting a clip reads the segments from the start of
        /// the first one - ffmpeg's concat demuxer cannot seek, and says so - so deleting a file a
        /// pending clip still needs would break it mid-read. The scheduler knows the oldest window
        /// it has not cut yet, and that is what is passed here.
        /// </summary>
        public int Trim(double keepFromBoardSec)
        {
            if (RetentionSec <= 0.0 || _entries.Count == 0) return 0;

            double newest = NewestSec;
            double cutoff = newest - RetentionSec;
            double reserved = Anchored ? ToRecordingSec(keepFromBoardSec) : double.NegativeInfinity;
            if (reserved < cutoff) cutoff = reserved;

            int removed = 0;
            while (_entries.Count > 0 && _entries[0].ToSec <= cutoff)
            {
                try { File.Delete(_entries[0].Path); }
                catch (IOException) { break; }              // in use: try again next tick
                catch (UnauthorizedAccessException) { break; }
                _entries.RemoveAt(0);
                removed++;
                Deleted++;
            }
            return removed;
        }

        /// <summary>Deletes every file still held. For a run that is finishing.</summary>
        public int DeleteAll()
        {
            int removed = 0;
            foreach (SegmentEntry e in _entries)
            {
                try { File.Delete(e.Path); removed++; Deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _entries.Clear();
            return removed;
        }

        /// <summary>The files held, oldest first.</summary>
        public IReadOnlyList<SegmentEntry> Entries => _entries;
    }
}
