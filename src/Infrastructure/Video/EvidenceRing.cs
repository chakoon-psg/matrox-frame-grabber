using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The last few seconds of frames, uncompressed, in host memory.
    ///
    /// It exists because an anomaly clip cannot answer the question it is kept for. The clip is cut
    /// from a rolling H.264 ring, so re-running the detector over it would not reproduce the
    /// deviation that was reported; only the original pixels would. And the original pixels cannot
    /// come from disk - a lossless ring written continuously is 278 MB/s across three channels,
    /// 24 TB a day, which kills a 750 TBW drive in a month. So they are kept in RAM, and only the
    /// window around a confirmed fault is ever written down.
    ///
    /// **The ring has to be longer than the window it serves.** The window brackets a fault that
    /// itself runs to the event cap, and it closes N seconds after that, so at the moment of
    /// writing the oldest frame needed is already 2N + cap seconds old - exactly where a ring of
    /// that length is about to overwrite. <see cref="FramesFor"/> does the sum and
    /// <see cref="UsableFraction"/> is the room left over to write in.
    ///
    /// Nothing here is copied out to be written. <see cref="Peek"/> hands back the slot's own array
    /// and <see cref="StillHolds"/> says afterwards whether it was still the right frame - copying
    /// 1.7 GB out first would put 0.85 GB/s of memory traffic beside the acquisition, and that is
    /// the load measured to cost frames.
    /// </summary>
    public sealed class EvidenceRing
    {
        private readonly object _gate = new object();
        private readonly byte[][] _frames;
        private readonly long[] _frameNumbers;
        private readonly double[] _times;
        private long _written;

        public EvidenceRing(int frames, int bytesPerFrame)
        {
            if (frames < 2) throw new ArgumentOutOfRangeException(nameof(frames));
            if (bytesPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(bytesPerFrame));

            BytesPerFrame = bytesPerFrame;
            _frames = new byte[frames][];
            _frameNumbers = new long[frames];
            _times = new double[frames];
            for (int i = 0; i < frames; i++)
            {
                _frames[i] = new byte[bytesPerFrame];
                _frameNumbers[i] = -1;

                // Touched now, before the grab starts, so the first pass through the ring does not
                // pay a page fault per 4 KB beside the acquisition. new byte[] gives back
                // demand-zero pages: committed on paper, not yet mapped.
                byte[] slot = _frames[i];
                for (int p = 0; p < slot.Length; p += 4096) slot[p] = 0;
            }
        }

        /// <summary>Slots, which is how many frames it can hold at once.</summary>
        public int Capacity => _frames.Length;

        /// <summary>Bytes in one frame.</summary>
        public int BytesPerFrame { get; }

        /// <summary>Frames put in since it was made.</summary>
        public long Written { get { lock (_gate) return _written; } }

        /// <summary>Bytes the ring occupies, for a settings window that has to admit the cost.</summary>
        public long Bytes => (long)Capacity * BytesPerFrame;

        /// <summary>
        /// Fraction of the ring a single window may span.
        ///
        /// The rest is what the writer runs in. Measured 2026-09-11: with the window at 97% of the
        /// ring, two dumps out of eight lost their first frame to the writer being overtaken - and
        /// said so, which is how this number came to exist.
        /// </summary>
        public const double UsableFraction = 0.66;

        /// <summary>
        /// How many slots a window of <paramref name="secondsEitherSide"/> needs at this rate.
        ///
        /// Three things go into it and leaving any one out costs frames:
        ///   - the window is 2N seconds, not N;
        ///   - the fault itself sits in the middle of it, and a fault runs to the event cap, so a
        ///     single event's window is 2N + cap;
        ///   - the write starts only when the window closes, by which time its head is the oldest
        ///     thing in the ring, so there has to be room left over to write in.
        ///
        /// The first version had only the first of those and the third as a flat half. It sized a
        /// +-2 s ring at 6 s against a window that could reach 6 s, and the measurement found it.
        /// </summary>
        public static int FramesFor(double secondsEitherSide, double fps, double maxEventSec = 0.0)
        {
            if (secondsEitherSide <= 0.0 || fps <= 1.0) return 0;
            double windowSec = secondsEitherSide * 2.0 + Math.Max(0.0, maxEventSec);
            double frames = windowSec / UsableFraction * fps;
            return frames < 2.0 ? 2 : (frames > int.MaxValue ? int.MaxValue : (int)Math.Ceiling(frames));
        }

        /// <summary>Bytes a ring for that window would take, before deciding to allocate one.</summary>
        public static long BytesFor(double secondsEitherSide, double fps, long bytesPerFrame,
                                    double maxEventSec = 0.0) =>
            (long)FramesFor(secondsEitherSide, fps, maxEventSec) * Math.Max(0, bytesPerFrame);

        /// <summary>
        /// The longest span this ring can serve and still be written safely.
        ///
        /// The scheduler merges events, so the window it asks for can be far wider than one
        /// fault's - measured, a run of 22 merged occurrences asked for 18.8 s. Asking the ring
        /// for more than this is how a dump loses its head, so the caller clamps to it and keeps
        /// the recent end, which is the part nearest the fault that closed the window.
        /// </summary>
        public double UsableSpanSec(double fps) =>
            fps > 1.0 ? Capacity * UsableFraction / fps : 0.0;

        /// <summary>
        /// Takes a copy of one frame. On the acquisition thread, so it is one memcpy and nothing
        /// else - no allocation, no locking beyond the index.
        /// </summary>
        public void Add(byte[] frame, long frameNumber, double boardTimeSec)
        {
            if (frame == null || frame.Length < BytesPerFrame) return;
            lock (_gate)
            {
                int slot = (int)(_written % _frames.Length);
                Buffer.BlockCopy(frame, 0, _frames[slot], 0, BytesPerFrame);
                _frameNumbers[slot] = frameNumber;
                _times[slot] = boardTimeSec;
                _written++;
            }
        }

        /// <summary>
        /// The frames whose board time falls in the window, as a range of frame numbers.
        ///
        /// False when the ring holds nothing in that span at all - which is what happens if the
        /// window is asked for too late, and is worth knowing rather than writing a short file.
        /// </summary>
        public bool TryWindow(double fromBoardSec, double toBoardSec,
                              out long firstFrame, out long lastFrame, out int count)
        {
            firstFrame = 0; lastFrame = 0; count = 0;
            lock (_gate)
            {
                bool any = false;
                for (int i = 0; i < _frames.Length; i++)
                {
                    if (_frameNumbers[i] < 0) continue;
                    double t = _times[i];
                    if (t < fromBoardSec || t > toBoardSec) continue;

                    long n = _frameNumbers[i];
                    if (!any) { firstFrame = n; lastFrame = n; any = true; }
                    else
                    {
                        if (n < firstFrame) firstFrame = n;
                        if (n > lastFrame) lastFrame = n;
                    }
                    count++;
                }
                return any;
            }
        }

        /// <summary>
        /// The slot holding this frame, or null when it has been overwritten.
        ///
        /// The array belongs to the ring: write it out at once and do not keep it. Call
        /// <see cref="StillHolds"/> afterwards to learn whether the ring overtook the reader
        /// mid-write, which is the one way this can produce a frame that is half of two.
        /// </summary>
        public byte[] Peek(long frameNumber, out double boardTimeSec)
        {
            lock (_gate)
            {
                int slot = SlotOf(frameNumber);
                if (slot < 0) { boardTimeSec = 0.0; return null; }
                boardTimeSec = _times[slot];
                return _frames[slot];
            }
        }

        /// <summary>Whether that frame is still the one in its slot.</summary>
        public bool StillHolds(long frameNumber)
        {
            lock (_gate) return SlotOf(frameNumber) >= 0;
        }

        /// <summary>The board time of a frame still held, or 0.</summary>
        public double TimeOf(long frameNumber)
        {
            lock (_gate)
            {
                int slot = SlotOf(frameNumber);
                return slot < 0 ? 0.0 : _times[slot];
            }
        }

        private int SlotOf(long frameNumber)
        {
            for (int i = 0; i < _frames.Length; i++)
                if (_frameNumbers[i] == frameNumber) return i;
            return -1;
        }
    }
}
