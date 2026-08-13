using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// Records raw video frames to an H.264 MP4 by piping them to an ffmpeg child process.
    /// Frames are queued and written on a dedicated thread so the acquisition thread is never
    /// blocked by ffmpeg/disk I/O (frames are dropped if the queue backs up).
    /// </summary>
    public sealed class FfmpegRecorder
    {
        private const int QueueCapacity = 8;

        private Process _proc;
        private Stream _stdin;
        private Thread _writer;
        private BlockingCollection<byte[]> _queue;
        private volatile bool _running;
        private readonly ConcurrentQueue<string> _stderrTail = new ConcurrentQueue<string>();

        public bool IsRunning => _running;
        public long DroppedFrames { get; private set; }

        /// <summary>Last error text (ffmpeg stderr tail / write failure), if any.</summary>
        public string LastError { get; private set; }

        /// <summary>Raised (on a background thread) when ffmpeg exits or a write fails while running.</summary>
        public event Action Failed;

        /// <summary>Invoked after each frame is written, so the caller can return the buffer to a pool.</summary>
        public Action<byte[]> FrameReturned;

        /// <summary>
        /// True if the writer queue has room for another frame. Callers should check this BEFORE
        /// doing the (expensive) frame extraction so the acquisition thread isn't slowed doing
        /// work that would only be dropped when the encoder is behind.
        /// </summary>
        public bool HasRoom => _running && _queue != null && _queue.Count < QueueCapacity;

        // Cache the resolved ffmpeg path (locating it can scan the WinGet tree).
        private static string _cachedPath;

        /// <summary>
        /// Finds ffmpeg.exe: the configured path, then PATH, then a winget install, then common
        /// locations. Returns the full path, or null if ffmpeg cannot be found. Result is cached.
        /// </summary>
        public static string ResolveFfmpegPath(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;
            if (_cachedPath != null && File.Exists(_cachedPath))
                return _cachedPath;

            string found = LocateFfmpeg();
            _cachedPath = found;
            return found;
        }

        private static string LocateFfmpeg()
        {
            try
            {
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }

            try
            {
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string links = Path.Combine(lad, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                if (File.Exists(links)) return links;
                string pkgs = Path.Combine(lad, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pkgs))
                {
                    string hit = Directory.EnumerateFiles(pkgs, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            catch { }

            foreach (string p in new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" })
                if (File.Exists(p)) return p;

            return null;
        }

        /// <summary>Launches ffmpeg for a raw-video pipe of the given geometry/format.</summary>
        /// <param name="pixFmt">ffmpeg input pixel format, e.g. "bgr24" or "gray".</param>
        public bool Start(string ffmpegPath, string outPath, int width, int height, double fps, string pixFmt, out string error)
        {
            error = null;
            try
            {
                string fpsStr = fps.ToString("0.###", CultureInfo.InvariantCulture);
                string args =
                    $"-hide_banner -loglevel error -f rawvideo -pixel_format {pixFmt} " +
                    $"-video_size {width}x{height} -framerate {fpsStr} -i pipe:0 -an " +
                    $"-vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                    $"-c:v libx264 -preset veryfast -pix_fmt yuv420p -movflags +faststart -y \"{outPath}\"";

                var psi = new ProcessStartInfo(ffmpegPath, args)
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _proc = Process.Start(psi);
                if (_proc == null) { error = "Failed to start ffmpeg."; return false; }

                _proc.EnableRaisingEvents = true;
                _proc.Exited += OnProcExited;
                _proc.ErrorDataReceived += OnStderr;   // keep a rolling tail + drain the pipe
                _proc.BeginErrorReadLine();

                _stdin = _proc.StandardInput.BaseStream;
                _queue = new BlockingCollection<byte[]>(QueueCapacity);
                _running = true;
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ffmpeg-writer" };
                _writer.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Cleanup();
                return false;
            }
        }

        private void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            _stderrTail.Enqueue(e.Data);
            while (_stderrTail.Count > 5) _stderrTail.TryDequeue(out _);
            LastError = string.Join(" | ", _stderrTail);
        }

        private void OnProcExited(object sender, EventArgs e)
        {
            if (_running)
            {
                _running = false;      // ffmpeg died unexpectedly while we were recording
                try { _queue?.CompleteAdding(); } catch { }
                Failed?.Invoke();
            }
        }

        /// <summary>Queues a frame for encoding; drops it if the writer is backed up.</summary>
        public void WriteFrame(byte[] frame)
        {
            if (!_running || _queue == null) { FrameReturned?.Invoke(frame); return; }
            if (!_queue.TryAdd(frame))
            {
                DroppedFrames++;
                FrameReturned?.Invoke(frame);   // recycle the dropped frame's buffer
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (byte[] frame in _queue.GetConsumingEnumerable())
                {
                    try { _stdin.Write(frame, 0, frame.Length); }
                    catch
                    {
                        FrameReturned?.Invoke(frame);
                        if (_running) { _running = false; LastError ??= "ffmpeg pipe write failed."; Failed?.Invoke(); }
                        break;
                    }
                    FrameReturned?.Invoke(frame);
                }
                try { _stdin.Flush(); } catch { }
            }
            catch { }
        }

        /// <summary>Stops encoding: flushes queued frames, closes the pipe, and finalizes the file.</summary>
        public void Stop()
        {
            bool wasRunning = _running;
            _running = false;
            try { _queue?.CompleteAdding(); } catch { }
            try { _writer?.Join(3000); } catch { }
            try { _stdin?.Close(); } catch { }     // EOF -> ffmpeg finalizes the mp4
            try
            {
                if (_proc != null && !_proc.WaitForExit(8000))
                {
                    _proc.Kill();
                    _proc.WaitForExit(2000);
                }
            }
            catch { }
            Cleanup();
        }

        private void Cleanup()
        {
            try { if (_proc != null) _proc.Exited -= OnProcExited; } catch { }
            try { if (_proc != null) _proc.ErrorDataReceived -= OnStderr; } catch { }
            try { _queue?.Dispose(); } catch { }
            try { _proc?.Dispose(); } catch { }
            _queue = null; _proc = null; _stdin = null; _writer = null;
        }
    }
}
