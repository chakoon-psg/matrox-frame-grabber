using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Owns one camera's MP4 recording: the ffmpeg process, the MIL capture/resize buffers,
    /// a reusable frame-buffer pool, and the per-frame feed. All acquisition-thread and
    /// UI-thread access is guarded so start/stop can't race the feed. Finalization runs on a
    /// background task so stopping never freezes the UI.
    /// </summary>
    public sealed class RecordingSession
    {
        private readonly MIL_ID _sysId;
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

        public RecordingSession(MIL_ID sysId) { _sysId = sysId; }

        public bool IsActive => _active;
        public bool Failed => _failed;
        public string FilePath { get; private set; }
        public string LastError { get; private set; }

        /// <summary>
        /// Starts recording <paramref name="sourceBuf"/>'s stream (geometry/format taken from it)
        /// to {baseName}_{timestamp}.mp4 in the output folder, applying the resolution preset.
        /// The buffers fed to <see cref="Feed"/> must have the same geometry as sourceBuf.
        /// </summary>
        public bool Start(MIL_ID sourceBuf, OutputSettings settings, string baseName, double fps, out string error)
        {
            error = null;
            if (_active) return false;

            string ffmpeg = FfmpegRecorder.ResolveFfmpegPath(settings?.FfmpegPath);
            if (ffmpeg == null) { error = "ffmpeg was not found. Install it or set the ffmpeg path in settings."; LastError = error; return false; }

            LastError = null;
            _failed = false;

            MIL_ID captureBuf = MIL.M_NULL, resizeBuf = MIL.M_NULL;
            MIL_ID b0 = MIL.M_NULL, b1 = MIL.M_NULL, b2 = MIL.M_NULL;
            FfmpegRecorder recorder = null;
            try
            {
                string path = Path.Combine(settings.EnsureFolder(), $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                MIL_INT band = MIL.MbufInquire(sourceBuf, MIL.M_SIZE_BAND, MIL.M_NULL);
                MIL_INT srcType = MIL.MbufInquire(sourceBuf, MIL.M_TYPE, MIL.M_NULL);
                MIL_INT srcBit = MIL.MbufInquire(sourceBuf, MIL.M_SIZE_BIT, MIL.M_NULL);
                long srcW = MIL.MbufInquire(sourceBuf, MIL.M_SIZE_X, MIL.M_NULL);
                long srcH = MIL.MbufInquire(sourceBuf, MIL.M_SIZE_Y, MIL.M_NULL);

                double scale = settings.ScaleFactorFor(srcH);
                long w = scale < 0.999 ? (long)(srcW * scale) : srcW;
                long h = scale < 0.999 ? (long)(srcH * scale) : srcH;
                w &= ~1L; h &= ~1L;
                if (w < 2 || h < 2) { error = "Resolution too small."; LastError = error; return false; }

                bool color = (long)band >= 3;
                string pixFmt = color ? "gbrp" : "gray";   // planar G,B,R (fed band-by-band, no packing)
                int bpp = color ? 3 : 1;
                int shift = (long)srcBit > 8 ? (int)((long)srcBit - 8) : 0;

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

                recorder = new FfmpegRecorder();
                recorder.FrameReturned = ReturnFrameBuffer;
                recorder.Failed += OnRecorderFailed;
                if (!recorder.Start(ffmpeg, path, (int)w, (int)h, fps, pixFmt, out string err))
                {
                    error = string.IsNullOrEmpty(err) ? "ffmpeg failed to launch." : err;
                    LastError = error;
                    recorder.Stop();
                    FreeBuffers(b0, b1, b2, captureBuf, resizeBuf);
                    return false;
                }

                lock (_lock)
                {
                    FilePath = path;
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

        /// <summary>Feeds one grabbed frame to the encoder (called on the acquisition thread).</summary>
        public void Feed(MIL_ID grabbedBuffer)
        {
            if (!_active) return;
            lock (_lock)
            {
                if (!_active || _recorder == null || _captureBuf == MIL.M_NULL)
                    return;
                if (!_recorder.HasRoom)   // encoder behind: skip extraction, keep the live view fast
                    return;
                try
                {
                    MIL_ID src = grabbedBuffer;
                    if (_resizeBuf != MIL.M_NULL)
                    {
                        MIL.MimResize(grabbedBuffer, _resizeBuf, _scaleX, _scaleY, MIL.M_BILINEAR);
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
                }
                catch
                {
                    // Drop this frame rather than tear down the recording mid-callback.
                }
            }
        }

        /// <summary>Stops recording; finalization (ffmpeg flush + buffer free) runs asynchronously.</summary>
        public void Stop()
        {
            FfmpegRecorder recorder;
            MIL_ID cap, rez, cb0, cb1, cb2;
            lock (_lock)
            {
                if (!_active) return;
                _active = false;
                recorder = _recorder; _recorder = null;
                cap = _captureBuf; _captureBuf = MIL.M_NULL;
                rez = _resizeBuf; _resizeBuf = MIL.M_NULL;
                cb0 = _b0; cb1 = _b1; cb2 = _b2;
                _b0 = _b1 = _b2 = MIL.M_NULL;
                _plane = null;
                _pool = null;
            }
            _finalizeTask = Task.Run(() =>
            {
                try { recorder?.Stop(); } catch { }
                FreeBuffers(cb0, cb1, cb2, cap, rez);   // children before parent
            });
        }

        /// <summary>Blocks until a pending async finalize completes (used before freeing the MIL system).</summary>
        public void WaitFinalize(int ms) { try { _finalizeTask?.Wait(ms); } catch { } }

        /// <summary>Status suffix like "  ● REC 01:23 (dropped 5)"; empty when not recording.</summary>
        public string StatusSuffix()
        {
            if (!_active) return "";
            int secs = (int)(DateTime.Now - _start).TotalSeconds;
            long dropped = _recorder?.DroppedFrames ?? 0;
            return $"  ● REC {secs / 60:00}:{secs % 60:00}" + (dropped > 0 ? $" (dropped {dropped})" : "");
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
