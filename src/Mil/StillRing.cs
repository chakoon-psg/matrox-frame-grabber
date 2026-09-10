using System;
using System.Diagnostics;
using System.IO;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Four MIL buffers per camera holding the frames worth keeping losslessly when an anomaly is
    /// confirmed, and the PNG export of them.
    ///
    /// This exists because the event clip cannot carry the evidence on its own. The clip is x264 at
    /// CRF 23, which is fine for a person to watch and not fine to re-measure from: re-running the
    /// detector over the file would not reproduce the depth that was reported. A lossless still of
    /// the deepest frame would.
    ///
    /// MIL is the right tool for exactly this and nothing else in the recording path. This
    /// installation's licence is M_LICENSE_LITE (modules 0x1, logged every start), so MseqAlloc and
    /// every compressed sequence are refused - see docs/adr/0001-ffmpeg-for-all-encoding.md. But
    /// MbufExport with M_PNG needs no compression licence and already runs in this app for
    /// snapshots, and it reads the MIL buffer directly: keeping a frame costs a MIL-to-MIL MbufCopy
    /// with no host readout at all, so none of the 462 us extraction the ffmpeg path pays.
    ///
    /// Frames are chosen in the acquisition hook and written from the stats tick, because PNG
    /// compression takes tens of milliseconds and the frame period is 8043 us. The lock is held
    /// only across a MIL copy, never across an export, for the same reason.
    /// </summary>
    public sealed class StillRing : IDisposable
    {
        /// <summary>
        /// Frames between refreshes of the healthy reference frame. 32 is 258 ms at 124.3 fps, so
        /// the reference is always a picture from before any fall the debounce is still counting.
        /// </summary>
        public const int ReferenceEveryFrames = 32;

        /// <summary>Which frame of an event a slot holds. The four together tell the whole story.</summary>
        public enum Slot
        {
            /// <summary>A healthy frame from up to 258 ms before the fall.</summary>
            Reference = 0,
            /// <summary>The frame that entered the event.</summary>
            Onset = 1,
            /// <summary>The deepest frame seen while the event was open.</summary>
            Worst = 2,
            /// <summary>The first normal frame after recovery.</summary>
            Recovered = 3,
        }

        private const int SlotCount = 4;

        private readonly object _lock = new object();
        private readonly MIL_ID[] _slot = new MIL_ID[SlotCount];
        private readonly long[] _frame = new long[SlotCount];
        private readonly double[] _time = new double[SlotCount];
        private readonly double[] _depth = new double[SlotCount];
        private readonly bool[] _filled = new bool[SlotCount];

        // Exports run outside the lock, so the slot is first copied here. That copy is MIL-to-MIL
        // and costs about what keeping a frame costs; holding the lock across a 20 ms PNG write
        // would stall the acquisition hook and show up as M_PROCESS_FRAME_MISSED.
        private MIL_ID _exportBuf = MIL.M_NULL;

        private double _copyUsSum;

        /// <summary>Frames kept. Three or four per event, plus one every 32 frames for the reference.</summary>
        public long Copies { get; private set; }

        /// <summary>Microseconds the last keep took. The frame period at 124.3 fps is 8043 us.</summary>
        public double LastCopyUs { get; private set; }

        /// <summary>Mean microseconds per keep.</summary>
        public double MeanCopyUs => Copies > 0 ? _copyUsSum / Copies : 0.0;

        /// <summary>Worst single keep. One over the frame period would cost a frame.</summary>
        public double MaxCopyUs { get; private set; }

        /// <summary>PNG files written.</summary>
        public long Exports { get; private set; }

        /// <summary>Milliseconds the slowest PNG write took. Never on the acquisition thread.</summary>
        public double MaxExportMs { get; private set; }

        /// <summary>Total milliseconds spent writing PNGs.</summary>
        public double ExportMsSum { get; private set; }

        /// <summary>True once the buffers exist.</summary>
        public bool IsAllocated { get; private set; }

        /// <summary>
        /// Allocates five buffers shaped like <paramref name="like"/> - the four slots and the
        /// export staging buffer. Deliberately M_PROC without M_GRAB: these are ordinary paged
        /// memory, so they leave the scarce non-paged/DMA pool to the grab ring.
        /// </summary>
        public bool Allocate(MIL_ID sysId, MIL_ID like, out string error)
        {
            error = null;
            if (IsAllocated) return true;
            try
            {
                MIL_INT band = MIL.MbufInquire(like, MIL.M_SIZE_BAND, MIL.M_NULL);
                MIL_INT type = MIL.MbufInquire(like, MIL.M_TYPE, MIL.M_NULL);
                MIL_INT w = MIL.MbufInquire(like, MIL.M_SIZE_X, MIL.M_NULL);
                MIL_INT h = MIL.MbufInquire(like, MIL.M_SIZE_Y, MIL.M_NULL);

                for (int i = 0; i < SlotCount; i++)
                {
                    MIL_ID b = MIL.M_NULL;
                    MIL.MbufAllocColor(sysId, band, w, h, type, MIL.M_IMAGE + MIL.M_PROC, ref b);
                    if (b == MIL.M_NULL) { error = "still buffer allocation returned M_NULL."; Free(); return false; }
                    MIL.MbufClear(b, 0);
                    _slot[i] = b;
                }
                MIL.MbufAllocColor(sysId, band, w, h, type, MIL.M_IMAGE + MIL.M_PROC, ref _exportBuf);
                if (_exportBuf == MIL.M_NULL) { error = "still export buffer allocation returned M_NULL."; Free(); return false; }
                MIL.MbufClear(_exportBuf, 0);

                IsAllocated = true;
                MilErrorLog.Note($"stills - {SlotCount + 1} buffers of {(long)w}x{(long)h}x{(long)band} allocated "
                               + $"({(SlotCount + 1) * (long)w * (long)h * (long)band / 1048576.0:F1} MiB, paged)");
                return true;
            }
            catch (MILException e)
            {
                error = e.Message;
                MilErrorLog.Write("allocate the still buffers", e);
                Free();
                return false;
            }
        }

        /// <summary>
        /// Keeps one frame in a slot. Called on the acquisition thread, so it does one MIL-to-MIL
        /// copy and nothing else - no host readout, no compression, no allocation.
        /// </summary>
        public void Keep(Slot slot, MIL_ID src, long frameNumber, double timeStampSec, double depth)
        {
            if (!IsAllocated || src == MIL.M_NULL) return;
            int i = (int)slot;
            long t0 = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                if (_slot[i] == MIL.M_NULL) return;
                try
                {
                    MIL.MbufCopy(src, _slot[i]);
                }
                catch (MILException)
                {
                    return;   // a lost still must never take the grab down with it
                }
                _frame[i] = frameNumber;
                _time[i] = timeStampSec;
                _depth[i] = depth;
                _filled[i] = true;
            }
            double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            LastCopyUs = us;
            _copyUsSum += us;
            if (us > MaxCopyUs) MaxCopyUs = us;
            Copies++;
        }

        /// <summary>Whether a slot currently holds a frame.</summary>
        public bool Has(Slot slot) => IsAllocated && _filled[(int)slot];

        /// <summary>
        /// Writes every filled slot as a PNG into <paramref name="folder"/> and returns how many
        /// were written. Called from the stats tick: PNG compression is tens of milliseconds, which
        /// is several frame periods.
        /// </summary>
        public int ExportAll(string folder, string baseName, out string error)
        {
            error = null;
            if (!IsAllocated) { error = "still buffers are not allocated."; return 0; }

            int written = 0;
            for (int i = 0; i < SlotCount; i++)
            {
                if (!_filled[i]) continue;

                long frame;
                double timeSec, depth;
                lock (_lock)
                {
                    if (!_filled[i] || _slot[i] == MIL.M_NULL) continue;
                    try
                    {
                        MIL.MbufCopy(_slot[i], _exportBuf);   // fast, and the only thing the lock covers
                    }
                    catch (MILException e) { error = e.Message; continue; }
                    frame = _frame[i];
                    timeSec = _time[i];
                    depth = _depth[i];
                }

                string name = $"{baseName}_{((Slot)i).ToString().ToLowerInvariant()}_f{frame}.png";
                string path = Path.Combine(folder, name);
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    MIL.MbufExport(path, MIL.M_PNG, _exportBuf);
                }
                catch (MILException e)
                {
                    error = e.Message;
                    MilErrorLog.Write($"export the {(Slot)i} still to PNG", e);
                    continue;
                }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1e3 / Stopwatch.Frequency;
                ExportMsSum += ms;
                if (ms > MaxExportMs) MaxExportMs = ms;
                Exports++;
                written++;

                MilErrorLog.Note($"still {(Slot)i} - frame {frame}, board {timeSec:F6} s, "
                               + $"depth {depth:0.####}, {ms:F1} ms, "
                               + $"{(File.Exists(path) ? new FileInfo(path).Length / 1024.0 : 0):F0} kB -> {name}");
            }
            return written;
        }

        /// <summary>Forgets the event slots, keeping the rolling reference.</summary>
        public void ClearEvent()
        {
            lock (_lock)
            {
                _filled[(int)Slot.Onset] = false;
                _filled[(int)Slot.Worst] = false;
                _filled[(int)Slot.Recovered] = false;
            }
        }

        public void Free()
        {
            lock (_lock)
            {
                for (int i = 0; i < SlotCount; i++)
                {
                    try { if (_slot[i] != MIL.M_NULL) MIL.MbufFree(_slot[i]); } catch { }
                    _slot[i] = MIL.M_NULL;
                    _filled[i] = false;
                }
                try { if (_exportBuf != MIL.M_NULL) MIL.MbufFree(_exportBuf); } catch { }
                _exportBuf = MIL.M_NULL;
                IsAllocated = false;
            }
        }

        public void Dispose() => Free();
    }
}
