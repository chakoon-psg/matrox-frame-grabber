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
    /// Writes one camera's video through a single ffmpeg process.
    ///
    /// Reading the frame out of MIL is not its job any more - that is FrameExtractor, so that two
    /// sinks can share one extraction. This sink either borrows the caller's extractor
    /// (<see cref="SharedFrames"/>, which is what the app does) or makes one of its own, and in
    /// both cases the frames it queues belong to that extractor until its writer is done with them.
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
    public sealed class FfmpegVideoSink : IVideoSink, IHostFrameSink
    {
        private readonly MIL_ID _sysId;
        private readonly string _ffmpegPath;
        private readonly object _lock = new object();

        private FfmpegRecorder _recorder;
        private FrameExtractor _source;            // borrowed (SharedFrames) or made here
        private bool _ownsSource;
        private volatile bool _active;
        private volatile bool _failed;
        private DateTime _start;
        private Task _finalizeTask;
        private long _fed, _skipped, _notWanted;
        private int _feedEveryNth = 1;
        private long _droppedAtStop;               // survives Stop(), which nulls the recorder
        private double _declaredFps, _elapsedAtStop, _feedUsSum, _maxFeedUs;
        private string[] _paths = Array.Empty<string>();
        private double[] _rates = Array.Empty<double>();
        private string[] _lists = Array.Empty<string>();
        private string _codecName = "ffmpeg";

        public FfmpegVideoSink(MIL_ID sysId, string ffmpegPath)
        {
            _sysId = sysId;
            _ffmpegPath = ffmpegPath;
        }

        /// <summary>
        /// An extractor to take frames from instead of reading them out here. Set before
        /// <see cref="Start"/>; null means this sink reads for itself.
        ///
        /// The app sets it so the session recording and the event tier share one read per frame -
        /// two reads cost 60-67 frames of 37,300 per channel, measured. A single sink on its own,
        /// like the MIL harness, leaves it null and pays for one read, which is the same cost it
        /// always had.
        /// </summary>
        public FrameExtractor SharedFrames { get; set; }

        /// <summary>
        /// ffmpeg plus whichever encoder the spec asked for. Not fixed any more: the operator
        /// chooses between H.264 and two bit-exact encodings, and a name that always said libx264
        /// would be the one place the log disagreed with what was written.
        /// </summary>
        public string Name => "ffmpeg/" + _codecName;
        public bool IsActive => _active;
        public bool Failed => _failed;
        public string LastError { get; private set; }
        public IReadOnlyList<string> FilePaths => _paths;
        public IReadOnlyList<double> FileRates => _rates;
        public IReadOnlyList<string> SegmentListPaths => _lists;

        public VideoSinkStats Stats => new VideoSinkStats(
            _fed, _skipped, _recorder?.DroppedFrames ?? _droppedAtStop, _declaredFps,
            _active ? (DateTime.Now - _start).TotalSeconds : _elapsedAtStop,
            _fed > 0 ? _feedUsSum / _fed : 0.0, _maxFeedUs, _notWanted);

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

            FfmpegRecorder recorder = null;
            FrameExtractor source = SharedFrames;
            bool ownsSource = false;
            try
            {
                // Borrowed or made here. Either way the geometry comes from it rather than being
                // worked out twice - the pixel format ffmpeg is told has to match the layout that
                // class writes, and one of them owning both is what keeps them from drifting.
                if (source == null)
                {
                    source = new FrameExtractor(_sysId);
                    ownsSource = true;
                    if (!source.Prepare(spec.Like, spec.Scale, out string prepErr))
                    {
                        error = prepErr ?? "Frame extraction could not be prepared.";
                        LastError = error;
                        source.Dispose();
                        return false;
                    }
                }
                else if (!source.IsReady)
                {
                    error = "The shared frame extractor is not prepared.";
                    LastError = error;
                    return false;
                }

                long w = source.Width, h = source.Height;
                bool color = source.Bands >= 3;

                // What this process is actually fed. One output taking every Nth frame means the
                // pipe carries source/N, so that is what -framerate must say - measured, declaring
                // the source rate while feeding a quarter of it kept 27 frames of 100. With more
                // than one output the pipe has to carry every frame and each output's -r selects,
                // which is the arrangement the two-output test locks.
                int feedEveryNth = spec.Outputs.Length == 1 ? spec.Outputs[0].EveryNthFrame : 1;
                double inputFps = VideoRatePolicy.FileFps(spec.SourceFps, feedEveryNth);

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputs = new List<FfmpegOutput>(spec.Outputs.Length);
                var paths = new List<string>(spec.Outputs.Length);
                var rates = new List<double>(spec.Outputs.Length);
                var lists = new List<string>(spec.Outputs.Length);
                foreach (VideoOutputSpec o in spec.Outputs)
                {
                    double fileFps = VideoRatePolicy.FileFps(spec.SourceFps, o.EveryNthFrame);
                    string stem = string.IsNullOrEmpty(o.Label)
                        ? $"{spec.BaseName}_{stamp}"
                        : $"{spec.BaseName}_{stamp}_{o.Label}";
                    // The extension picks the muxer, so it comes from the encoding: utvideo
                    // into .mp4 is refused and rawvideo into .mkv too.
                    string ext = VideoCodecs.Extension(o.Encoding, o.Container);
                    string path = Path.Combine(spec.Folder,
                        o.IsSegmented ? $"{stem}_%05d.{ext}" : $"{stem}.{ext}");
                    string list = o.IsSegmented ? Path.Combine(spec.Folder, stem + ".csv") : null;

                    outputs.Add(new FfmpegOutput(path, fileFps,
                        VideoRatePolicy.KeyframeInterval(fileFps, o.KeyframeSeconds),
                        o.SegmentSeconds, list, o.Encoding, o.Container));
                    paths.Add(path);
                    rates.Add(fileFps);
                    lists.Add(list);
                }

                string args = FfmpegArgs.Build((int)w, (int)h, color ? 3 : 1, inputFps, outputs);

                recorder = new FfmpegRecorder();
                // Returned to the extractor, not to a pool of this sink's own: the array may still
                // be held by the other sink, and only the hold count knows when it is free.
                FrameExtractor releaseTo = source;
                recorder.FrameReturned = releaseTo.Release;
                recorder.Failed += OnRecorderFailed;
                if (!recorder.Start(_ffmpegPath, args, out string err))
                {
                    error = string.IsNullOrEmpty(err) ? "ffmpeg failed to launch." : err;
                    LastError = error;
                    recorder.Stop();
                    if (ownsSource) source.Dispose();
                    return false;
                }

                lock (_lock)
                {
                    _paths = paths.ToArray();
                    _rates = rates.ToArray();
                    _lists = lists.ToArray();
                    _source = source;
                    _ownsSource = ownsSource;
                    _feedEveryNth = feedEveryNth;
                    _recorder = recorder;
                    _start = DateTime.Now;
                    _declaredFps = spec.SourceFps;
                    _codecName = VideoCodecs.Name(spec.Outputs[0].Encoding, color ? 3 : 1);
                    _fed = 0;
                    _skipped = 0;
                    _notWanted = 0;
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
                if (ownsSource) source?.Dispose();
                return false;
            }
        }

        /// <summary>
        /// Reads one frame out and hands it to ffmpeg. On the acquisition thread.
        ///
        /// This is the standalone path - one sink, its own extractor. The app does not use it: the
        /// channel extracts once and calls <see cref="FeedShared"/> on both sinks, because two
        /// reads per frame cost 60-67 frames of 37,300 per channel (measured 2026-09-10).
        ///
        /// Every frame goes in whatever the outputs asked for: one pipe serves them all, and each
        /// output's -r makes ffmpeg select the frames it wants. So EveryNthFrame reaches this
        /// backend only as the rate a file declares, never as a frame this method should skip.
        /// </summary>
        public void Feed(MIL_ID buffer, long frameNumber)
        {
            if (!_active) return;
            FrameExtractor source = _source;
            if (source == null) return;

            if (!VideoRatePolicy.ShouldFeed(frameNumber, _feedEveryNth)) { _notWanted++; return; }
            // Nothing is read out while the encoder has no room for it - the live view comes first.
            if (!WantsFrame(frameNumber)) { _skipped++; return; }

            byte[] frame = source.Extract(buffer);
            if (frame == null) { _skipped++; return; }
            Accept(frame, source);
            source.Release(frame);      // the hold Extract handed back
        }

        /// <summary>
        /// Takes a frame somebody else read out, in the layout this sink's ffmpeg was told to
        /// expect. On the acquisition thread.
        ///
        /// The caller owns a hold on <paramref name="frame"/> and releases it afterwards; this adds
        /// its own before queueing, so the array cannot go back to the pool while ffmpeg is still
        /// being fed from it. Returns whether it took the frame, which is only of interest to a
        /// caller counting what its sinks did with it.
        /// </summary>
        public bool FeedShared(byte[] frame, long frameNumber)
        {
            if (!_active) return false;
            FrameExtractor source = _source;
            if (source == null) return false;
            // Not this sink's frame. Counted apart from a skip: a session tier at every fourth
            // frame turns three away by design, and calling those skips would read as a fault.
            if (!VideoRatePolicy.ShouldFeed(frameNumber, _feedEveryNth))
            {
                _notWanted++;
                return false;
            }
            if (frame == null)
            {
                // The caller had nothing to give. Counted here rather than nowhere: a dry pool or
                // an encoder that was behind is a frame missing from this file, and the count is
                // the only place that says so.
                _skipped++;
                return false;
            }
            return Accept(frame, source);
        }

        /// <summary>
        /// Whether this frame would be queued: the encoder has room and the frame is one this sink
        /// takes. See IHostFrameSink.WantsFrame.
        /// </summary>
        public bool WantsFrame(long frameNumber)
        {
            FfmpegRecorder r = _recorder;
            return _active && r != null && r.HasRoom
                && VideoRatePolicy.ShouldFeed(frameNumber, _feedEveryNth);
        }

        /// <summary>
        /// Adds a hold and queues the frame. The hold goes on *before* the queue, because the
        /// writer thread can finish and release before this method returns.
        /// </summary>
        private bool Accept(byte[] frame, FrameExtractor source)
        {
            lock (_lock)
            {
                if (!_active || _recorder == null) return false;
                if (!_recorder.HasRoom)
                {
                    _skipped++;
                    return false;
                }
                long t0 = Stopwatch.GetTimestamp();
                if (!source.AddHold(frame)) return false;   // not that extractor's, or already free
                try
                {
                    _recorder.WriteFrame(frame);            // releases the hold when written
                }
                catch
                {
                    // Drop this frame rather than tear down the recording mid-callback - and give
                    // the hold back, or the pool loses a slot for the rest of the run.
                    source.Release(frame);
                    return false;
                }
                double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                _feedUsSum += us;
                if (us > _maxFeedUs) _maxFeedUs = us;
                _fed++;
                return true;
            }
        }

        public void Stop()
        {
            FfmpegRecorder recorder;
            FrameExtractor source;
            bool ownsSource;
            lock (_lock)
            {
                if (!_active) return;
                _active = false;
                _elapsedAtStop = (DateTime.Now - _start).TotalSeconds;
                recorder = _recorder; _recorder = null;
                source = _source; _source = null;
                ownsSource = _ownsSource; _ownsSource = false;
            }
            // Accept returns early once _active is false and it is the only caller of WriteFrame,
            // so no further drops can be counted after the lock above - this read is final.
            _droppedAtStop = recorder?.DroppedFrames ?? 0;

            _finalizeTask = Task.Run(() =>
            {
                // Stop first, then the buffers: Stop drains the queue, and every frame it drains
                // is released back to the extractor. A borrowed extractor is the caller's to
                // dispose - it is probably still feeding the other sink.
                try { recorder?.Stop(); } catch { }
                if (ownsSource) source?.Dispose();
            });
        }

        public void WaitFinalize(int ms) { try { _finalizeTask?.Wait(ms); } catch { } }

        public void Dispose()
        {
            Stop();
            WaitFinalize(15000);
        }

        private void OnRecorderFailed()
        {
            _failed = true;
            LastError = _recorder?.LastError ?? "ffmpeg stopped unexpectedly.";
        }

    }
}
