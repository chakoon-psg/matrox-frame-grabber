using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// A fixed set of host-memory frames that more than one sink can be writing at once.
    ///
    /// It exists because reading a frame out of MIL twice cost frames. Measured 2026-09-10 over two
    /// five-minute runs at 124.3 fps on three channels: with the session recording and the event
    /// tier each extracting for itself, every channel lost 60-67 frames of 37,300; with the session
    /// recording off, none. The per-frame extract peaked at 11549 us against an 8197 us frame
    /// period, and adding the second sink did not double each extract but quadrupled it - 354 to
    /// 1416 us - so it was memory contention, not just repeated work. Two extracts per frame across
    /// three channels is 1.68 GB/s of host reads beside the 0.85 GB/s the acquisition DMA writes
    /// through the same path.
    ///
    /// So the frame is read out once and both sinks are handed the same array. That needs a count
    /// of who is still holding it, because each sink's writer thread finishes whenever ffmpeg lets
    /// it and the array cannot be reused until the last of them is done.
    ///
    /// The protocol, which is what the tests pin down:
    ///   - <see cref="Take"/> gives the caller a frame with one hold - its own.
    ///   - each sink that accepts the frame calls <see cref="AddHold"/> *before* queueing it, so the
    ///     count cannot reach zero while another sink is still being offered it.
    ///   - everyone calls <see cref="Release"/> exactly once, including the caller, and including a
    ///     sink that turns the frame away after adding its hold.
    ///
    /// Fixed rather than growing: an unbounded pool under a stalled encoder is a memory leak with a
    /// slow fuse, and a pool that cannot hand one out is a fact worth counting (<see cref="Exhausted"/>)
    /// rather than hiding behind an allocation.
    /// </summary>
    public sealed class SharedFramePool
    {
        private readonly object _gate = new object();
        private readonly byte[][] _frames;
        private readonly int[] _holds;
        private int _inUse;
        private long _exhausted;

        /// <summary>
        /// Allocates <paramref name="frames"/> buffers of <paramref name="bytesPerFrame"/>.
        ///
        /// Size it for what can be in flight: each ffmpeg writer queues up to 8 frames, so two
        /// sinks plus the one the acquisition thread is holding needs about 20 to never run dry.
        /// </summary>
        public SharedFramePool(int frames, int bytesPerFrame)
        {
            if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
            if (bytesPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(bytesPerFrame));

            BytesPerFrame = bytesPerFrame;
            _frames = new byte[frames][];
            _holds = new int[frames];
            for (int i = 0; i < frames; i++)
                _frames[i] = new byte[bytesPerFrame];
        }

        /// <summary>How many frames it holds.</summary>
        public int Capacity => _frames.Length;

        /// <summary>Bytes in each one.</summary>
        public int BytesPerFrame { get; }

        /// <summary>Frames currently held by somebody.</summary>
        public int InUse { get { lock (_gate) return _inUse; } }

        /// <summary>
        /// Times <see cref="Take"/> came back empty. Non-zero means a sink was so far behind that
        /// its whole queue was full of frames nobody had written yet - the reading, not the frame,
        /// is what was lost.
        /// </summary>
        public long Exhausted { get { lock (_gate) return _exhausted; } }

        /// <summary>
        /// A free frame with one hold on it - the caller's - or null when every frame is out.
        ///
        /// The array is not cleared. It is about to be overwritten by the extraction, and clearing
        /// 2.26 MiB per frame is the sort of cost this class exists to remove.
        /// </summary>
        public byte[] Take()
        {
            lock (_gate)
            {
                for (int i = 0; i < _holds.Length; i++)
                {
                    if (_holds[i] != 0) continue;
                    _holds[i] = 1;
                    _inUse++;
                    return _frames[i];
                }
                _exhausted++;
                return null;
            }
        }

        /// <summary>
        /// Records one more holder. False for a frame that is not this pool's, or that nobody holds
        /// any more - both of which mean the caller is about to write into a buffer it does not own,
        /// so they are worth a false rather than a silent success.
        /// </summary>
        public bool AddHold(byte[] frame)
        {
            lock (_gate)
            {
                int i = IndexOf(frame);
                if (i < 0 || _holds[i] <= 0) return false;
                _holds[i]++;
                return true;
            }
        }

        /// <summary>
        /// Gives up one hold, freeing the frame when the last holder does.
        ///
        /// A frame that is not this pool's, or that is already free, is ignored: a double release
        /// must not put the same buffer in two hands, which is the one failure here that would
        /// corrupt a recording rather than just slow it down.
        /// </summary>
        public void Release(byte[] frame)
        {
            lock (_gate)
            {
                int i = IndexOf(frame);
                if (i < 0 || _holds[i] <= 0) return;
                if (--_holds[i] == 0) _inUse--;
            }
        }

        /// <summary>Holders of one frame, for tests and for a stats line.</summary>
        public int HoldsOn(byte[] frame)
        {
            lock (_gate)
            {
                int i = IndexOf(frame);
                return i < 0 ? 0 : _holds[i];
            }
        }

        // Reference identity, and a scan rather than a dictionary: the pool is about twenty entries
        // and this runs twice per frame per sink, so the scan is cheaper than hashing an array
        // reference - and it allocates nothing, which matters on the acquisition thread.
        private int IndexOf(byte[] frame)
        {
            if (frame == null) return -1;
            for (int i = 0; i < _frames.Length; i++)
                if (ReferenceEquals(_frames[i], frame)) return i;
            return -1;
        }
    }
}
