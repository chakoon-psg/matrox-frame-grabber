using System;
using System.Collections.Generic;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// Writes video through MIL's own sequence encoder instead of an external process.
    ///
    /// NOT IMPLEMENTED AND NEVER RUN. <see cref="Implemented"/> is the flag that says so, and it
    /// is deliberately separate from whether MIL accepts a compression context here - this
    /// installation's licence refuses one, and if that refusal also gated the option then an
    /// implementation delivered for this machine could never be tried on it. The file exists
    /// because the board's supplier is better placed to write it than this project is, and because
    /// a contract with only one implementation is not a contract. See tools/MilVideoSink for the
    /// standalone harness it can be developed and measured in, without the application.
    ///
    /// Why it is worth having at all: <see cref="Feed"/> receives a MIL_ID, and MseqFeed takes that
    /// buffer directly. The ffmpeg backend has to read every frame out to host memory first - 462 us
    /// per frame, measured - and this path would pay none of it. For three cameras at decim 2 that
    /// saving buys nothing (the frame period is 8043 us and 91% of it is idle), but at full
    /// resolution or more channels it stops being free.
    ///
    /// What an implementation has to do, in order:
    ///   1. MseqAlloc(M_DEFAULT, M_DEFAULT, M_SEQ_COMPRESS, format, M_DEFAULT, ref seq)
    ///        - format M_DEFAULT reaches a JPEG-sequence licence check on this installation;
    ///          M_FILE_FORMAT_H264 / _MP4 exist in Mil.h but are not this argument.
    ///   2. MseqDefine(seq, M_SEQ_OUTPUT(0) + M_SEQ_DEST, M_FILE, path, M_FILE_FORMAT_MP4)
    ///        - M_SEQ_OUTPUT is a C macro (M_SEQ_CONTAINER_OUTPUT | index &lt;&lt; 18); whether the
    ///          .NET binding exposes the pieces is unverified.
    ///   3. MseqControl(seq, M_CONTEXT, M_STREAM_FRAME_RATE, fps)
    ///        - must be the rate the camera can deliver, not the rate it was asked for. Getting
    ///          this wrong made an ffmpeg recording of 120.0 s read as 81.07 s.
    ///      MseqControl(seq, M_CONTEXT, M_STREAM_GROUP_OF_PICTURE_SIZE, keyframeInterval)
    ///        - a window has to be cut out of these files afterwards.
    ///   4. MseqProcess(seq, M_START, M_ASYNCHRONOUS)
    ///   5. MseqFeed(seq, buffer, M_DEFAULT) per frame, from the acquisition thread
    ///   6. MseqProcess(seq, M_STOP, M_WAIT), then MseqFree
    ///
    /// The acceptance numbers are in tools/MilVideoSink/ACCEPTANCE.md. The one that matters most:
    /// step 5 runs inside the frame period, so a run of 60 s must not increase
    /// M_PROCESS_FRAME_MISSED.
    /// </summary>
    public sealed class MilSeqVideoSink : IVideoSink
    {
        /// <summary>
        /// Flip to true when this sink actually encodes. It gates the MIL option in settings, and
        /// gates it separately from whether MIL accepts a context here - the two are different
        /// facts, and conflating them is what would make a delivered implementation impossible to
        /// run on a machine whose licence refuses compression.
        /// </summary>
        // static readonly, not const: a const false lets the compiler prove the rest of the
        // readiness check unreachable, and this project keeps warnings at zero.
        public static readonly bool Implemented = false;

        private readonly MIL_ID _sysId;
        private MIL_ID _seq = MIL.M_NULL;
        private string[] _paths = Array.Empty<string>();

        public MilSeqVideoSink(MIL_ID sysId) { _sysId = sysId; }

        public string Name => "MIL/Mseq";

        /// <summary>
        /// Whether this installation's MIL accepts a compression context, and MIL's own reason when
        /// it does not.
        ///
        /// Advisory, not a gate: it asks for the default output format, and an implementation that
        /// uses another may well disagree with it. It lives here rather than in the application's
        /// startup so that changing the allocation below changes the probe with it.
        /// </summary>
        public static bool Probe(out string reason)
        {
            MIL_ID seq = MIL.M_NULL;
            try
            {
                MIL.MseqAlloc(MIL.M_DEFAULT, MIL.M_DEFAULT, MIL.M_SEQ_COMPRESS,
                              unchecked((uint)MIL.M_DEFAULT), MIL.M_DEFAULT, ref seq);
                if (seq != MIL.M_NULL) { reason = "ok"; return true; }

                // MseqAlloc returns M_NULL rather than throwing, and errors are print-disabled
                // process-wide, so the reason has to be read out deliberately or it is lost.
                reason = CurrentMilError();
                return false;
            }
            catch (MILException e)
            {
                reason = e.Message.Trim();
                return false;
            }
            finally
            {
                try { if (seq != MIL.M_NULL) MIL.MseqFree(seq); } catch { }
            }
        }

        /// <summary>MIL's current error as text, since print-disabled failures leave nothing behind.</summary>
        private static string CurrentMilError()
        {
            try
            {
                MIL_INT code = 0;
                MIL.MappGetError(MIL.M_DEFAULT, MIL.M_CURRENT, ref code);

                var msg = new System.Text.StringBuilder(MIL.M_ERROR_MESSAGE_SIZE);
                MIL.MappGetError(MIL.M_DEFAULT, MIL.M_CURRENT + MIL.M_MESSAGE, msg);
                var sub = new System.Text.StringBuilder(MIL.M_ERROR_MESSAGE_SIZE);
                MIL.MappGetError(MIL.M_DEFAULT, MIL.M_CURRENT_SUB_1 + MIL.M_MESSAGE, sub);

                string a = msg.ToString().Trim();
                string b = sub.ToString().Trim();
                return $"MIL error 0x{(long)code:x}: {a}" + (b.Length > 0 ? " / " + b : string.Empty);
            }
            catch (MILException e) { return "could not read the MIL error: " + e.Message.Trim(); }
        }

        public bool IsActive => false;
        public bool Failed => false;
        public string LastError { get; private set; }
        public IReadOnlyList<string> FilePaths => _paths;
        public IReadOnlyList<double> FileRates => Array.Empty<double>();
        public IReadOnlyList<string> SegmentListPaths => Array.Empty<string>();
        public VideoSinkStats Stats => default;

        /// <summary>
        /// Allocates a compression context and stops there. Returns false with MIL's own reason,
        /// which is the whole useful output of this file today: the factory needs to know not to
        /// offer this sink, and the reason belongs in the log rather than in a person's memory.
        /// </summary>
        public bool Start(VideoStreamSpec spec, out string error)
        {
            error = null;
            try
            {
                MIL.MseqAlloc(MIL.M_DEFAULT, MIL.M_DEFAULT, MIL.M_SEQ_COMPRESS,
                              unchecked((uint)MIL.M_DEFAULT), MIL.M_DEFAULT, ref _seq);
                if (_seq == MIL.M_NULL)
                {
                    error = "MIL cannot allocate a compression context here.";
                    LastError = error;
                    return false;
                }

                // A context exists, which is further than this has ever got. Stopping rather than
                // guessing the rest: a half-written encoder that produces a file nobody checked is
                // worse than an honest refusal, because the file would be an anomaly's only record.
                MIL.MseqFree(_seq);
                _seq = MIL.M_NULL;
                error = "MIL can encode here, but this sink is not implemented yet - "
                      + "see tools/MilVideoSink.";
                LastError = error;
                return false;
            }
            catch (MILException e)
            {
                error = e.Message.Trim();
                LastError = error;
                return false;
            }
        }

        /// <summary>Would be MseqFeed(seq, buffer, M_DEFAULT). Never reached: Start always fails.</summary>
        public void Feed(MIL_ID buffer, long frameNumber) { }

        public void Stop()
        {
            if (_seq == MIL.M_NULL) return;
            try { MIL.MseqFree(_seq); } catch (MILException e) { MilErrorLog.Write("free the MIL sequence context", e); }
            _seq = MIL.M_NULL;
        }

        public void WaitFinalize(int ms) { }

        public void Dispose() => Stop();
    }
}
