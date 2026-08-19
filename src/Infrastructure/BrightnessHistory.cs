using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>One brightness reading: Rec.601 luma plus the two clipping proportions.</summary>
    public readonly struct BrightnessSample
    {
        /// <summary>Mean Rec.601 luma, 0-255.</summary>
        public readonly float Luma;
        /// <summary>Percentage of sampled pixels with any band at 255.</summary>
        public readonly float ClippedPct;
        /// <summary>Percentage of sampled pixels with every band at 0.</summary>
        public readonly float BlackPct;

        public BrightnessSample(float luma, float clippedPct, float blackPct)
        {
            Luma = luma;
            ClippedPct = clippedPct;
            BlackPct = blackPct;
        }
    }

    /// <summary>
    /// A fixed-size ring of brightness readings for one channel. Sized for two minutes at the
    /// 500 ms stats tick; the oldest reading is dropped once it is full.
    /// </summary>
    public sealed class BrightnessHistory
    {
        /// <summary>240 readings = 120 s at the 500 ms stats tick.</summary>
        public const int Capacity = 240;

        private readonly BrightnessSample[] _items = new BrightnessSample[Capacity];
        private int _start;   // index of the oldest item
        private int _count;

        /// <summary>How many readings are currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        public bool HasData => _count > 0;

        /// <summary>The most recent reading. Meaningless when <see cref="HasData"/> is false.</summary>
        public BrightnessSample Latest =>
            _count == 0 ? default : _items[(_start + _count - 1) % Capacity];

        public void Add(BrightnessSample sample)
        {
            if (_count < Capacity)
            {
                _items[(_start + _count) % Capacity] = sample;
                _count++;
            }
            else
            {
                _items[_start] = sample;
                _start = (_start + 1) % Capacity;
            }
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        /// <summary>
        /// Copies the readings oldest-first into <paramref name="destination"/>, which must hold at
        /// least <see cref="Count"/> entries. Returns how many were written.
        /// </summary>
        public int CopyTo(BrightnessSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length < _count) throw new ArgumentException("destination too small", nameof(destination));

            for (int i = 0; i < _count; i++)
                destination[i] = _items[(_start + i) % Capacity];
            return _count;
        }
    }
}
