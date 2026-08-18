using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Continuous, segmented lossless RAW-Bayer recording for one camera.
    ///
    /// The acquisition hook (single producer thread) writes every frame to the current segment's
    /// <see cref="RawFrameWriter"/> via <see cref="Rent"/> + <see cref="Feed"/>. Every
    /// <c>segmentSeconds</c> the segment rolls over: the finished .raw is handed to a background
    /// convert thread that transcodes it to a color MP4 (ffmpeg: raw Bayer → libx264) in the output
    /// folder and deletes the .raw. This keeps the on-disk RAW footprint bounded while producing a
    /// series of ordinary MP4 files, so recording can run indefinitely (disk/throughput permitting).
    ///
    /// Thread model: the hook thread exclusively owns the *current* writer (Rent/Feed/Roll — no lock
    /// needed). A dedicated convert thread exclusively owns *finished* writers pulled off a queue.
    /// The two never touch the same writer, so the hand-off is race-free.
    /// </summary>
    public sealed class RawSegmentSession : IDisposable
    {
        private readonly string _ffmpeg, _scratchDir, _outputDir, _baseName, _pixFmt;
        private readonly int _width, _height, _frameBytes, _segmentSeconds;

        private readonly BlockingCollection<Seg> _convertQueue = new BlockingCollection<Seg>();
        private readonly Thread _convertThread;

        // Owned by the hook thread only:
        private RawFrameWriter _cur;
        private string _curPath;
        private DateTime _segStart;
        private int _segIndex;

        private int _segmentsCompleted;
        public int SegmentsCompleted => Volatile.Read(ref _segmentsCompleted);
        public int SegmentIndex => _segIndex;                 // 1-based current segment number
        public double CurrentSegmentSeconds => (DateTime.Now - _segStart).TotalSeconds;
        public volatile bool Failed;
        public string LastError;
        public string LastMp4;

        private readonly struct Seg
        {
            public readonly RawFrameWriter Writer;
            public readonly string RawPath;
            public readonly DateTime Start;
            public readonly double Seconds;
            public Seg(RawFrameWriter w, string path, DateTime start, double seconds)
            { Writer = w; RawPath = path; Start = start; Seconds = seconds; }
        }

        /// <param name="pixFmt">
        /// ffmpeg raw pixel format matching the sensor's mosaic (e.g. "bayer_rggb8"). Must come from
        /// the digitizer — guessing it swaps the colours of every converted segment.
        /// </param>
        public RawSegmentSession(string ffmpegPath, string scratchDir, string outputDir, string baseName,
            int width, int height, int frameBytes, int segmentSeconds, string pixFmt)
        {
            _ffmpeg = ffmpegPath;
            _scratchDir = scratchDir;
            _outputDir = outputDir;
            _baseName = baseName;
            _width = width;
            _height = height;
            _frameBytes = frameBytes;
            _segmentSeconds = Math.Max(5, segmentSeconds);
            _pixFmt = string.IsNullOrWhiteSpace(pixFmt) ? "bayer_rggb8" : pixFmt;

            Directory.CreateDirectory(_scratchDir);
            Directory.CreateDirectory(_outputDir);
            OpenSegment();                                     // first segment (throws if it can't open)

            _convertThread = new Thread(ConvertLoop) { IsBackground = true, Name = "RawSegmentConvert" };
            _convertThread.Start();
        }

        // ----- Hook thread (producer) -----

        /// <summary>Rolls to a new segment if the current one is full, then rents a frame buffer.</summary>
        public byte[] Rent()
        {
            if (_cur == null) return new byte[_frameBytes];    // failed open; frame is discarded in Feed
            if (CurrentSegmentSeconds >= _segmentSeconds)
                Roll();
            return _cur != null ? _cur.Rent() : new byte[_frameBytes];
        }

        /// <summary>Queues a filled buffer into the current segment.</summary>
        public void Feed(byte[] buf) => _cur?.Enqueue(buf);

        private void OpenSegment()
        {
            _segIndex++;
            _segStart = DateTime.Now;
            _curPath = Path.Combine(_scratchDir,
                $"{_baseName}_{_segStart:yyyyMMdd_HHmmss}_p{_segIndex:000}.raw");
            _cur = new RawFrameWriter(_curPath, _frameBytes);
        }

        private void Roll()
        {
            RawFrameWriter old = _cur;
            string oldPath = _curPath;
            DateTime oldStart = _segStart;
            double secs = Math.Max(0.001, (DateTime.Now - _segStart).TotalSeconds);

            try { OpenSegment(); }
            catch (Exception e) { Failed = true; LastError = e.Message; _cur = null; }

            // Hand the finished segment to the convert pipeline (finalizer drains + transcodes it).
            _convertQueue.Add(new Seg(old, oldPath, oldStart, secs));
        }

        // ----- Control -----

        /// <summary>Call AFTER the grab has stopped (no more Feed): finalizes the last segment.</summary>
        public void Finish()
        {
            if (_cur != null)
            {
                double secs = Math.Max(0.001, (DateTime.Now - _segStart).TotalSeconds);
                _convertQueue.Add(new Seg(_cur, _curPath, _segStart, secs));
                _cur = null;
            }
            _convertQueue.CompleteAdding();
        }

        /// <summary>Waits up to <paramref name="ms"/> for pending conversions; true if all finished.</summary>
        public bool WaitConversions(int ms) => _convertThread.Join(ms);

        // ----- Convert thread (consumer) -----

        private void ConvertLoop()
        {
            foreach (Seg seg in _convertQueue.GetConsumingEnumerable())
            {
                try
                {
                    seg.Writer.CompleteAndWait(60000);
                    long frames = seg.Writer.FramesWritten;
                    if (seg.Writer.Failed) { Failed = true; LastError = seg.Writer.LastError; }
                    seg.Writer.Dispose();

                    if (frames > 0 && !seg.Writer.Failed)
                        ConvertOne(seg.RawPath, seg.Start, frames, seg.Seconds);
                    else
                        TryDelete(seg.RawPath);
                }
                catch (Exception e)
                {
                    Failed = true;
                    LastError = e.Message;
                }
            }
        }

        private void ConvertOne(string rawPath, DateTime start, long frames, double seconds)
        {
            string mp4 = Path.Combine(_outputDir, $"{_baseName}_{start:yyyyMMdd_HHmmss}.mp4");
            double fps = frames / Math.Max(0.001, seconds);

            var psi = new ProcessStartInfo(_ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (string a in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "rawvideo", "-pixel_format", _pixFmt,
                "-video_size", $"{_width}x{_height}",
                "-framerate", fps.ToString("F3", CultureInfo.InvariantCulture),
                "-i", rawPath,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-movflags", "+faststart", "-y", mp4
            })
                psi.ArgumentList.Add(a);

            try
            {
                using Process proc = Process.Start(psi);
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode == 0)
                {
                    LastMp4 = mp4;
                    Interlocked.Increment(ref _segmentsCompleted);
                    TryDelete(rawPath);              // keep only the compressed MP4
                }
                else
                {
                    Failed = true;
                    LastError = "ffmpeg: " + Tail(stderr);   // .raw is kept for manual recovery
                }
            }
            catch (Exception e)
            {
                Failed = true;
                LastError = e.Message;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (path != null) File.Delete(path); } catch { /* ignore */ }
        }

        private static string Tail(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string[] lines = s.Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        public void Dispose()
        {
            try { if (!_convertQueue.IsAddingCompleted) _convertQueue.CompleteAdding(); } catch { }
            _convertQueue.Dispose();
        }
    }
}
