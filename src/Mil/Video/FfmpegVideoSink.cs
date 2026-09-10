using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// Writes one camera's video through a single ffmpeg process: the MIL capture and resize
    /// buffers, a reusable frame-buffer pool, and the per-frame extraction.
    ///
    /// One process serves every output. ffmpeg reads the pipe once and feeds each output's encoder
    /// chain, so a segmented event tier at 124.316 fps and a long session file at 31.079 fps cost
    /// one extraction between them and no shared-ownership problem - the frame buffer belongs to
    /// exactly one writer. Two processes would need either a 2.26 MiB copy per frame or reference
    /// counting, and would double the pipes and exit handlers for nothing. Measured 2026-09-10:
    /// two outputs from one pipe kept 2486 of 2486 frames in the segments and wrote the session
    /// file at 31079/1000 over the same 20.0 s.
    ///
    /// All acquisition-thread and UI-thread access is guarded so start/stop cannot race the feed.
    /// Finalization runs on a background task so stopping never freezes the UI.
    /// </summary>
    public sealed class FfmpegVideoSink : IVideoSink
    {
        private readonly MIL_ID _sysId;
        private readonly string _ffmpegPath;
        private readonly object _lock = new object();

        private FfmpegRecorder _recorder;
        private MIL_ID _captureBuf = MIL.M_NULL;   // planar 3-band / mono at encode size
        private MIL_ID _resizeBuf = MIL.M_NULL;    // downscale intermediate (M_NULL if no resize)
        private MIL_ID _b0 = MIL.M_NULL, _b1 = MIL.M_NULL, _b2 = MIL.M_NULL;  // per-band children (color)
        private byte[] _plane;                     // reused single-band scratch (w*h) for color reads
        private ConcurrentQueue<byte[]> _pool;
        private int _w, _h, _bpp, _shift;
        private double _scaleX = 1.0, _scaleY = 1.0;
        private volatile bool _active;
        private volatile bool _failed;
        private DateTime _start;
        private Task _finalizeTask;
        private long _fed, _skipped;
        private long _droppedAtStop;               // survives Stop(), which nulls the recorder
        private double _declaredFps, _elapsedAtStop, _feedUsSum, _maxFeedUs;
        private string[] _paths = Array.Empty<string>();
        private double[] _rates = Array.Empty<double>();

        public FfmpegVideoSink(MIL_ID sysId, string ffmpegPath)
        {
            _sysId = sysId;
            _ffmpegPath = ffmpegPath;
        }

        public string Name => "ffmpeg/libx264";
        public bool IsActive => _active;
        public bool Failed => _failed;
        public string LastError { get; private set; }
        public IReadOnlyList<string> FilePaths => _paths;
        public IReadOnlyList<double> FileRates => _rates;

        public VideoSinkStats Stats => new VideoSinkStats(
            _fed, _skipped, _recorder?.DroppedFrames ?? _droppedAtStop, _declaredFps,
            _active ? (DateTime.Now - _start).TotalSeconds : _elapsedAtStop,
            _fed > 0 ? _feedUsSum / _fed : 0.0, _maxFeedUs);

        /// <summary>Status suffix like "  ● REC 01:23 (dropped 5)"; empty when not recording.</summary>
        public string StatusSuffix()
        {
            if (!_active) return "";
            int secs = (int)(DateTime.Now - _start).TotalSeconds;
            long dropped = _recorder?.DroppedFrames ?? 0;
            return $"  ● REC {secs / 60:00}:{secs % 60:00}" + (dropped > 0 ? $" (dropped {dropped})" : "");
        }

        public bool Start(VideoStreamSpec spec, out string error)
        {
            error = null;
            if (_active) return false;

            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                error = "ffmpeg was not found. Install it or set the ffmpeg path in settings.";
                LastError = error;
                return false;
            }

            LastError = null;
            _failed = false;

            MIL_ID captureBuf = MIL.M_NULL, resizeBuf = MIL.M_NULL;
            MIL_ID b0 = MIL.M_NULL, b1 = MIL.M_NULL, b2 = MIL.M_NULL;
            FfmpegRecorder recorder = null;
            try
            {
                MIL_INT band = MIL.MbufInquire(spec.Like, MIL.M_SIZE_BAND, MIL.M_NULL);
                MIL_INT srcType = MIL.MbufInquire(spec.Like, MIL.M_TYPE, MIL.M_NULL);
                MIL_INT srcBit = MIL.MbufInquire(spec.Like, MIL.M_SIZE_BIT, MIL.M_NULL);
                long srcW = MIL.MbufInquire(spec.Like, MIL.M_SIZE_X, MIL.M_NULL);
                long srcH = MIL.MbufInquire(spec.Like, MIL.M_SIZE_Y, MIL.M_NULL);

                double scale = spec.Scale;
                long w = scale < 0.999 ? (long)(srcW * scale) : srcW;
                long h = scale < 0.999 ? (long)(srcH * scale) : srcH;
                w &= ~1L; h &= ~1L;
                if (w < 2 || h < 2) { error = "Resolution too small."; LastError = error; return false; }

                bool color = (long)band >= 3;
                // Planar G,B,R fed band by band, never packed - see the MbufGetColor trap.
                int bpp = color ? 3 : 1;
                int shift = (long)srcBit > 8 ? (int)((long)srcBit - 8) : 0;

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputs = new List<FfmpegOutput>(spec.Outputs.Length);
                var paths = new List<string>(spec.Outputs.Length);
                var rates = new List<double>(spec.Outputs.Length);
                foreach (VideoOutputSpec o in spec.Outputs)
                {
                    double fileFps = VideoRatePolicy.FileFps(spec.SourceFps, o.EveryNthFrame);
                    string stem = string.IsNullOrEmpty(o.Label)
                        ? $"{spec.BaseName}_{stamp}"
                        : $"{spec.BaseName}_{stamp}_{o.Label}";
                    string path = Path.Combine(spec.Folder,
                        o.IsSegmented ? stem + "_%05d.mp4" : stem + ".mp4");
                    string list = o.IsSegmented ? Path.Combine(spec.Folder, stem + ".csv") : null;

                    outputs.Add(new FfmpegOutput(path, fileFps,
                        VideoRatePolicy.KeyframeInterval(fileFps, o.KeyframeSeconds),
                        o.SegmentSeconds, list));
                    paths.Add(path);
                    rates.Add(fileFps);
                }

                // Planar capture buffer. Color frames are read out one band at a time (MbufGet on a
                // single-band child) and fed to ffmpeg as planar gbrp — MbufGetColor's packing paths
                // either hang or return zeros on this buffer, and plain MbufGet only yields band 0.
                MIL.MbufAllocColor(_sysId, color ? 3 : 1, w, h, 8 + MIL.M_UNSIGNED,
                    MIL.M_IMAGE + MIL.M_PROC, ref captureBuf);
                if (color)
                {
                    MIL.MbufChildColor(captureBuf, 0, ref b0);   // band 0 (R)
                    MIL.MbufChildColor(captureBuf, 1, ref b1);   // band 1 (G)
                    MIL.MbufChildColor(captureBuf, 2, ref b2);   // band 2 (B)
                }
                if (scale < 0.999)
                    MIL.MbufAllocColor(_sysId, band, w, h, srcType, MIL.M_IMAGE + MIL.M_PROC, ref resizeBuf);

                string args = FfmpegArgs.Build((int)w, (int)h, color ? 3 : 1, spec.SourceFps, outputs);

                recorder = new FfmpegRecorder();
                recorder.FrameReturned = ReturnFrameBuffer;
                recorder.Failed += OnRecorderFailed;
                if (!recorder.Start(_ffmpegPath, args, out string err))
                {
                    error = string.IsNullOrEmpty(err) ? "ffmpeg failed to launch." : err;
                    LastError = error;
                    recorder.Stop();
                    FreeBuffers(b0, b1, b2, captureBuf, resizeBuf);
                    return false;
                }

                lock (_lock)
                {
                    _paths = paths.ToArray();
                    _rates = rates.ToArray();
                    _captureBuf = captureBuf;
                    _resizeBuf = resizeBuf;
                    _b0 = b0; _b1 = b1; _b2 = b2;
                    _plane = color ? new byte[(int)(w * h)] : null;
                    _bpp = bpp; _w = (int)w; _h = (int)h; _shift = shift;
                    _scaleX = (double)w / srcW;
                    _scaleY = (double)h / srcH;
                    _pool = new ConcurrentQueue<byte[]>();
                    _recorder = recorder;
                    _start = DateTime.Now;
                    _declaredFps = spec.SourceFps;
                    _fed = 0;
                    _skipped = 0;
                    _droppedAtStop = 0;
                    _elapsedAtStop = 0.0;
                    _feedUsSum = 0.0;
                    _maxFeedUs = 0.0;
                    _active = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message; LastError = error;
                try { recorder?.Stop(); } catch { }
                FreeBuffers(b0, b1, b2, captureBuf, resizeBuf);
                return false;
            }
        }

        /// <summary>
        /// Extracts one frame and hands it to ffmpeg. On the acquisition thread.
        ///
        /// Every frame goes in whatever the outputs asked for: one pipe serves them all, and each
        /// output's -r makes ffmpeg select the frames it wants. So EveryNthFrame reaches this
        /// backend only as the rate a file declares, never as a frame this method should skip.
        /// </summary>
        public void Feed(MIL_ID buffer, long frameNumber)
        {
            if (!_active) return;
            lock (_lock)
            {
                if (!_active || _recorder == null || _captureBuf == MIL.M_NULL)
                    return;
                if (!_recorder.HasRoom)   // encoder behind: skip extraction, keep the live view fast
                {
                    _skipped++;
                    return;
                }
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    MIL_ID src = buffer;
                    if (_resizeBuf != MIL.M_NULL)
                    {
                        MIL.MimResize(buffer, _resizeBuf, _scaleX, _scaleY, MIL.M_BILINEAR);
                        src = _resizeBuf;
                    }
                    if (_shift > 0)
                        MIL.MimShift(src, _captureBuf, -_shift);
                    else
                        MIL.MbufCopy(src, _captureBuf);

                    byte[] frame = _pool != null && _pool.TryDequeue(out byte[] b) ? b : new byte[_w * _h * _bpp];
                    if (_bpp == 3)
                    {
                        int wh = _w * _h;                                   // planar gbrp: [G][B][R]
                        MIL.MbufGet(_b1, _plane); Buffer.BlockCopy(_plane, 0, frame, 0, wh);        // G
                        MIL.MbufGet(_b2, _plane); Buffer.BlockCopy(_plane, 0, frame, wh, wh);       // B
                        MIL.MbufGet(_b0, _plane); Buffer.BlockCopy(_plane, 0, frame, 2 * wh, wh);   // R
                    }
                    else
                        MIL.MbufGet(_captureBuf, frame);
                    _recorder.WriteFrame(frame);

                    double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                    _feedUsSum += us;
                    if (us > _maxFeedUs) _maxFeedUs = us;
                    _fed++;
                }
                catch
                {
                    // Drop this frame rather than tear down the recording mid-callback.
                }
            }
        }

        public void Stop()
        {
            FfmpegRecorder recorder;
            MIL_ID cap, rez, cb0, cb1, cb2;
            lock (_lock)
            {
                if (!_active) return;
                _active = false;
                _elapsedAtStop = (DateTime.Now - _start).TotalSeconds;
                recorder = _recorder; _recorder = null;
                cap = _captureBuf; _captureBuf = MIL.M_NULL;
                rez = _resizeBuf; _resizeBuf = MIL.M_NULL;
                cb0 = _b0; cb1 = _b1; cb2 = _b2;
                _b0 = _b1 = _b2 = MIL.M_NULL;
                _plane = null;
                _pool = null;
            }
            // Feed returns early once _active is false and it is the only caller of WriteFrame,
            // so no further drops can be counted after the lock above - this read is final.
            _droppedAtStop = recorder?.DroppedFrames ?? 0;

            _finalizeTask = Task.Run(() =>
            {
                try { recorder?.Stop(); } catch { }
                FreeBuffers(cb0, cb1, cb2, cap, rez);   // children before parent
            });
        }

        public void WaitFinalize(int ms) { try { _finalizeTask?.Wait(ms); } catch { } }

        public void Dispose()
        {
            Stop();
            WaitFinalize(15000);
        }

        private void ReturnFrameBuffer(byte[] buf)
        {
            var pool = _pool;
            if (pool != null && buf != null && buf.Length == _w * _h * _bpp && pool.Count < 12)
                pool.Enqueue(buf);
        }

        private void OnRecorderFailed()
        {
            _failed = true;
            LastError = _recorder?.LastError ?? "ffmpeg stopped unexpectedly.";
        }

        /// <summary>Frees band children before their parent capture buffer, then the resize buffer.</summary>
        private static void FreeBuffers(MIL_ID b0, MIL_ID b1, MIL_ID b2, MIL_ID cap, MIL_ID rez)
        {
            try { if (b0 != MIL.M_NULL) MIL.MbufFree(b0); } catch { }
            try { if (b1 != MIL.M_NULL) MIL.MbufFree(b1); } catch { }
            try { if (b2 != MIL.M_NULL) MIL.MbufFree(b2); } catch { }
            try { if (cap != MIL.M_NULL) MIL.MbufFree(cap); } catch { }
            try { if (rez != MIL.M_NULL) MIL.MbufFree(rez); } catch { }
        }
    }
}
