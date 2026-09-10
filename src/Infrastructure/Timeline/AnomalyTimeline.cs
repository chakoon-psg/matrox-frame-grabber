using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The recent anomalies of one channel, detected and rejected together, for the strip to draw.
    ///
    /// Both verdicts in one ring because the strip shows both and the reader needs them in one
    /// order: on the measured 30-minute run two detections fell inside a burst of sixteen
    /// rejections, and which was which - and when - is the whole content of that part of the lane.
    ///
    /// Added and read on the UI thread only. Confirmed events and rejections both reach it from the
    /// 500 ms stats tick, which is also where the strip redraws, so there is nothing to lock.
    /// </summary>
    public sealed class AnomalyTimeline
    {
        /// <summary>
        /// Events held. The 30-minute static run produced 74 per channel, and the strip shows ten
        /// minutes of them, so this covers far more than is ever on screen.
        /// </summary>
        public const int Capacity = 256;

        private readonly TimelineEvent[] _items = new TimelineEvent[Capacity];
        private int _next;
        private int _count;

        /// <summary>Events currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        /// <summary>Everything ever added, including what has since been overwritten.</summary>
        public long Added { get; private set; }

        public void Add(AnomalyEvent e, bool rejected)
        {
            _items[_next] = new TimelineEvent(e, rejected);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            Added++;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _next = 0;
            _count = 0;
            Added = 0;
        }

        /// <summary>
        /// Copies what is held into <paramref name="buffer"/>, oldest first, and returns how many.
        ///
        /// Into a caller's array rather than returning a new one: the strip redraws twice a second
        /// and can keep one scratch buffer, the same arrangement BrightnessHistory uses.
        /// </summary>
        public int CopyTo(TimelineEvent[] buffer)
        {
            if (buffer == null) return 0;
            int n = Math.Min(_count, buffer.Length);
            int start = (_next - _count + Capacity) % Capacity;
            for (int i = 0; i < n; i++)
                buffer[i] = _items[(start + i) % Capacity];
            return n;
        }

        /// <summary>Detected events held, for a lane's own count.</summary>
        public int DetectedCount => CountWhere(rejected: false);

        /// <summary>Rejected events held.</summary>
        public int RejectedCount => CountWhere(rejected: true);

        private int CountWhere(bool rejected)
        {
            int n = 0, start = (_next - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
                if (_items[(start + i) % Capacity].Rejected == rejected) n++;
            return n;
        }
    }
}
