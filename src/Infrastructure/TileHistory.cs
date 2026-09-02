using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// A rolling window of recent tile grids, kept so that a confirmed anomaly can be written out
    /// with the frames that led up to it.
    ///
    /// The lead-up is the whole point. An event is only confirmed after the debounce has passed,
    /// which is 161 ms at 124.3 fps, and by then the picture has recovered -- a snapshot taken when
    /// the report arrives shows a healthy screen. Whatever happened has to have been kept.
    ///
    /// Frames cannot be. At 2.26 MiB each, three seconds of one channel is 843 MiB, and the copy
    /// would have to happen inside the acquisition hook. Tile means can: the reducer has already
    /// computed them, and 64 floats is 256 bytes, so eight seconds of history costs about 280 kB a
    /// channel. That is also the more useful record for the question being asked, which is what
    /// kind of darkening this was rather than what the room looked like -- 64 values at full frame
    /// rate separate a uniform fall from one that swept across, and separate both from tiles moving
    /// in different directions.
    ///
    /// Written from the acquisition thread and read from the stats tick, so both sides lock. The
    /// write is 64 floats and the read is one array copy, so neither holds it long.
    /// </summary>
    public sealed class TileHistory
    {
        /// <summary>
        /// Frames held. 1024 is about 8.2 s at 124.3 fps -- comfortably longer than the longest
        /// event the detector can report (the 2 s cap) plus its debounce plus the up-to-500 ms wait
        /// for the stats tick that does the writing.
        /// </summary>
        public const int Capacity = 1024;

        private readonly float[] _means = new float[Capacity * TileGrid.TileCount];
        private readonly long[] _frames = new long[Capacity];
        private readonly double[] _times = new double[Capacity];
        private readonly object _lock = new object();

        private int _next;      // where the next frame goes
        private int _count;     // frames held, up to Capacity

        /// <summary>Frames currently held.</summary>
        public int Count { get { lock (_lock) return _count; } }

        /// <summary>
        /// Records one frame. Called per frame on the acquisition thread, so it reads the grid and
        /// stores plain numbers rather than keeping the grid: the caller reuses one instance.
        /// </summary>
        public void Add(TileGrid grid, double timeStampSec)
        {
            if (grid == null) return;

            lock (_lock)
            {
                int at = _next * TileGrid.TileCount;
                for (int i = 0; i < TileGrid.TileCount; i++)
                    _means[at + i] = grid.IsPopulated(i) ? (float)grid.Mean(i) : float.NaN;

                _frames[_next] = grid.FrameNumber;
                _times[_next] = timeStampSec;

                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _next = 0;
                _count = 0;
            }
        }

        /// <summary>
        /// Writes every held frame whose number falls in [<paramref name="fromFrame"/>,
        /// <paramref name="toFrame"/>] to a CSV, oldest first. Returns the path, or null when
        /// nothing in range was still held or the file could not be written.
        ///
        /// An unpopulated tile is written empty rather than as a zero. A tile nothing was sampled
        /// from is not a dark tile, and a reader averaging a column of zeros would conclude the
        /// opposite of what happened.
        /// </summary>
        public string Write(string folder, string label, long fromFrame, long toFrame)
        {
            float[] means;
            long[] frames;
            double[] times;
            int count, next;

            lock (_lock)
            {
                if (_count == 0) return null;
                means = (float[])_means.Clone();
                frames = (long[])_frames.Clone();
                times = (double[])_times.Clone();
                count = _count;
                next = _next;
            }

            int first = (next - count + Capacity) % Capacity;
            var inv = CultureInfo.InvariantCulture;

            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, label + ".csv");

                using (var writer = new StreamWriter(path, append: false, Encoding.UTF8))
                {
                    var header = new StringBuilder("frame,board_time_s");
                    for (int i = 0; i < TileGrid.TileCount; i++)
                        header.Append(inv, $",t{i / TileGrid.Columns}{i % TileGrid.Columns}");
                    writer.WriteLine(header.ToString());

                    int written = 0;
                    var row = new StringBuilder(512);
                    for (int n = 0; n < count; n++)
                    {
                        int slot = (first + n) % Capacity;
                        long frame = frames[slot];
                        if (frame < fromFrame || frame > toFrame) continue;

                        row.Clear();
                        row.Append(frame.ToString(inv)).Append(',')
                           .Append(times[slot].ToString("F6", inv));

                        int at = slot * TileGrid.TileCount;
                        for (int i = 0; i < TileGrid.TileCount; i++)
                        {
                            row.Append(',');
                            float v = means[at + i];
                            if (!float.IsNaN(v)) row.Append(v.ToString("F2", inv));
                        }
                        writer.WriteLine(row.ToString());
                        written++;
                    }

                    if (written == 0)
                    {
                        writer.Dispose();
                        try { File.Delete(path); } catch (IOException) { }
                        return null;
                    }
                }

                return path;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                MilErrorLog.Write("tile history: writing an event window", e);
                return null;
            }
        }
    }
}
