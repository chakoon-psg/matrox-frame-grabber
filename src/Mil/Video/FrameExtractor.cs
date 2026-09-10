using System;
using System.Diagnostics;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil.Video
{
    /// <summary>
    /// Reads grabbed frames out of MIL into host memory, once per frame, for however many sinks
    /// want them.
    ///
    /// It exists because doing it twice cost frames. Measured 2026-09-10, two five-minute runs at
    /// 124.3 fps on three channels: with the session recording and the event tier each extracting
    /// for itself, every channel lost 60-67 frames of 37,300; with the session recording off, none.
    /// The extract peaked at 11549 us against an 8197 us frame period, and the second sink did not
    /// double each extract but quadrupled it (354 to 1416 us), which is memory contention rather
    /// than repeated work - two extracts per frame across three channels is 1.68 GB/s of host reads
    /// beside the 0.85 GB/s the acquisition DMA writes through the same path.
    ///
    /// The extraction itself is unchanged, and every step of it is load-bearing:
    ///   - MbufCopy into a buffer this class owns, because the band children have to belong to a
    ///     buffer whose lifetime we control, and because a >8-bit source needs MimShift.
    ///   - MbufGet per band into a scratch plane, then into the frame at its planar offset. Colour
    ///     is fed as gbrp because MbufGetColor's packing paths either hang or return zeros on these
    ///     buffers - see CLAUDE.md - so the layout here has to match what ffmpeg is told.
    ///   - children freed before the parent.
    ///
    /// Ownership is the new part: <see cref="Extract"/> hands back an array with one hold, each
    /// sink that accepts it adds a hold before queueing, and everyone releases exactly once. See
    /// <see cref="SharedFramePool"/>, where that counting lives and is tested.
    /// </summary>
    public sealed class FrameExtractor : IDisposable
    {
        /// <summary>
        /// Frames in the pool. Each ffmpeg writer queues up to 8, so two sinks plus the one the
        /// acquisition thread is holding fits in 20 with room to spare. At 1024x772x3 that is
        /// 45 MiB of paged host memory per channel - the same order as the two per-sink pools it
        /// replaces, which grew to about 9 buffers each.
        /// </summary>
        public const int PoolFrames = 20;

        private readonly MIL_ID _sysId;
        private readonly object _gate = new object();

        private SharedFramePool _pool;
        private MIL_ID _captureBuf = MIL.M_NULL;   // planar 3-band / mono at encode size
        private MIL_ID _resizeBuf = MIL.M_NULL;    // downscale intermediate (M_NULL if no resize)
        private MIL_ID _b0 = MIL.M_NULL, _b1 = MIL.M_NULL, _b2 = MIL.M_NULL;
        private byte[] _plane;                     // reused single-band scratch (w*h)
        private int _w, _h, _bpp, _shift;
        private double _scaleX = 1.0, _scaleY = 1.0;
        private long _fed, _failures;
        private double _usSum, _maxUs;

        public FrameExtractor(MIL_ID sysId)
        {
            _sysId = sysId;
        }

        /// <summary>Width of the extracted frame, after any scale. Zero until prepared.</summary>
        public int Width { get { lock (_gate) return _w; } }

        /// <summary>Height of the extracted frame, after any scale.</summary>
        public int Height { get { lock (_gate) return _h; } }

        /// <summary>3 for colour (planar gbrp), 1 for mono (gray).</summary>
        public int Bands { get { lock (_gate) return _bpp; } }

        /// <summary>True between a successful <see cref="Prepare"/> and <see cref="Dispose"/>.</summary>
        public bool IsReady { get { lock (_gate) return _pool != null; } }

        /// <summary>Frames read out.</summary>
        public long Fed { get { lock (_gate) return _fed; } }

        /// <summary>Frames a MIL call refused. Non-zero means somebody is being fed nothing.</summary>
        public long Failures { get { lock (_gate) return _failures; } }

        /// <summary>Times the pool was dry - every frame still held by a writer that is behind.</summary>
        public long Exhausted => _pool?.Exhausted ?? 0;

        /// <summary>Mean microseconds per extraction, which is the cost being shared.</summary>
        public double MeanUs { get { lock (_gate) return _fed > 0 ? _usSum / _fed : 0.0; } }

        /// <summary>Worst extraction. Compare it against the frame period - that is the loss.</summary>
        public double MaxUs { get { lock (_gate) return _maxUs; } }

        /// <summary>
        /// Allocates the MIL buffers and the pool for frames shaped like <paramref name="like"/>.
        ///
        /// <paramref name="scale"/> below 1 resizes first; the app always passes 1.0 (see
        /// CLAUDE.md - the frame size follows the acquisition), and the parameter stays because the
        /// sink contract has it and a harness may use it.
        /// </summary>
        public bool Prepare(MIL_ID like, double scale, out string error)
        {
            error = null;
            MIL_ID captureBuf = MIL.M_NULL, resizeBuf = MIL.M_NULL;
            MIL_ID b0 = MIL.M_NULL, b1 = MIL.M_NULL, b2 = MIL.M_NULL;
            try
            {
                MIL_INT band = MIL.MbufInquire(like, MIL.M_SIZE_BAND, MIL.M_NULL);
                MIL_INT srcType = MIL.MbufInquire(like, MIL.M_TYPE, MIL.M_NULL);
                MIL_INT srcBit = MIL.MbufInquire(like, MIL.M_SIZE_BIT, MIL.M_NULL);
                long srcW = MIL.MbufInquire(like, MIL.M_SIZE_X, MIL.M_NULL);
                long srcH = MIL.MbufInquire(like, MIL.M_SIZE_Y, MIL.M_NULL);

                long w = scale < 0.999 ? (long)(srcW * scale) : srcW;
                long h = scale < 0.999 ? (long)(srcH * scale) : srcH;
                w &= ~1L; h &= ~1L;
                if (w < 2 || h < 2) { error = "Resolution too small."; return false; }

                bool color = (long)band >= 3;
                int bpp = color ? 3 : 1;
                int shift = (long)srcBit > 8 ? (int)((long)srcBit - 8) : 0;

                // Planar capture buffer. Colour frames are read out one band at a time (MbufGet on
                // a single-band child) and fed to ffmpeg as planar gbrp - MbufGetColor's packing
                // paths either hang or return zeros on these buffers, and plain MbufGet only yields
                // band 0.
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

                lock (_gate)
                {
                    _captureBuf = captureBuf;
                    _resizeBuf = resizeBuf;
                    _b0 = b0; _b1 = b1; _b2 = b2;
                    _plane = color ? new byte[(int)(w * h)] : null;
                    _bpp = bpp; _w = (int)w; _h = (int)h; _shift = shift;
                    _scaleX = (double)w / srcW;
                    _scaleY = (double)h / srcH;
                    _fed = 0; _failures = 0; _usSum = 0.0; _maxUs = 0.0;
                    _pool = new SharedFramePool(PoolFrames, (int)(w * h * bpp));
                }
                return true;
            }
            catch (MILException e)
            {
                error = "MIL buffer allocation failed: " + e.Message;
                Free(b0, b1, b2, captureBuf, resizeBuf);
                return false;
            }
        }

        /// <summary>
        /// Reads one frame into a pooled array and hands it over with one hold - the caller's.
        ///
        /// Null when the pool is dry (every frame still held by a writer that is behind) or when a
        /// MIL call refused. Both are counted rather than thrown: this runs on the acquisition
        /// thread, inside the frame period, and an exception here would take the grab with it.
        /// </summary>
        public byte[] Extract(MIL_ID buffer)
        {
            lock (_gate)
            {
                SharedFramePool pool = _pool;
                if (pool == null || _captureBuf == MIL.M_NULL) return null;

                byte[] frame = pool.Take();
                if (frame == null) return null;      // counted by the pool

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

                    if (_bpp == 3)
                    {
                        int wh = _w * _h;                                   // planar gbrp: [G][B][R]
                        MIL.MbufGet(_b1, _plane); Buffer.BlockCopy(_plane, 0, frame, 0, wh);        // G
                        MIL.MbufGet(_b2, _plane); Buffer.BlockCopy(_plane, 0, frame, wh, wh);       // B
                        MIL.MbufGet(_b0, _plane); Buffer.BlockCopy(_plane, 0, frame, 2 * wh, wh);   // R
                    }
                    else
                        MIL.MbufGet(_captureBuf, frame);

                    double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                    _usSum += us;
                    if (us > _maxUs) _maxUs = us;
                    _fed++;
                    return frame;
                }
                catch
                {
                    // Give the frame back rather than leaking a pool slot per failure, which would
                    // turn one refused MIL call into a sink that never records again.
                    pool.Release(frame);
                    _failures++;
                    return null;
                }
            }
        }

        /// <summary>One more holder of this frame. Called by a sink before it queues it.</summary>
        public bool AddHold(byte[] frame) => _pool?.AddHold(frame) ?? false;

        /// <summary>One holder is finished. The frame returns to the pool when the last one is.</summary>
        public void Release(byte[] frame) => _pool?.Release(frame);

        /// <summary>Frames currently out with a writer, for a stats line.</summary>
        public int InUse => _pool?.InUse ?? 0;

        /// <summary>
        /// Frees the MIL buffers. Anything still queued in a sink keeps its array - the arrays are
        /// managed memory and outlive this - so a frame being written while the grab stops is
        /// written whole rather than torn.
        /// </summary>
        public void Dispose()
        {
            MIL_ID cap, rez, cb0, cb1, cb2;
            lock (_gate)
            {
                cap = _captureBuf; _captureBuf = MIL.M_NULL;
                rez = _resizeBuf; _resizeBuf = MIL.M_NULL;
                cb0 = _b0; cb1 = _b1; cb2 = _b2;
                _b0 = _b1 = _b2 = MIL.M_NULL;
                _plane = null;
                _pool = null;
            }
            Free(cb0, cb1, cb2, cap, rez);
        }

        /// <summary>Band children before their parent, then the resize buffer.</summary>
        private static void Free(MIL_ID b0, MIL_ID b1, MIL_ID b2, MIL_ID cap, MIL_ID rez)
        {
            try { if (b0 != MIL.M_NULL) MIL.MbufFree(b0); } catch { }
            try { if (b1 != MIL.M_NULL) MIL.MbufFree(b1); } catch { }
            try { if (b2 != MIL.M_NULL) MIL.MbufFree(b2); } catch { }
            try { if (cap != MIL.M_NULL) MIL.MbufFree(cap); } catch { }
            try { if (rez != MIL.M_NULL) MIL.MbufFree(rez); } catch { }
        }
    }
}
