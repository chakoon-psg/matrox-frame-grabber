using System;
using System.IO;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The window written around an event is the only record of what the tiles were doing before
    /// it. If it is wrong the event is undiagnosable, and nothing about the file says so.
    /// </summary>
    public class TileHistoryTests : IDisposable
    {
        private readonly string _folder =
            Path.Combine(Path.GetTempPath(), "mfg-tile-history-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }

        static TileGrid Grid(long frame, double value, int populated = TileGrid.TileCount)
        {
            var g = new TileGrid { FrameNumber = frame };
            for (int i = 0; i < populated; i++)
                g.Accumulate(i, (long)value, (long)(value * value), 1);
            return g;
        }

        static void Fill(TileHistory h, long from, long to, double value = 100)
        {
            for (long n = from; n <= to; n++)
                h.Add(Grid(n, value), n * 0.008);
        }

        [Fact]
        public void Add_StopsGrowingAtCapacity()
        {
            var history = new TileHistory();
            Fill(history, 1, TileHistory.Capacity + 500);
            Assert.Equal(TileHistory.Capacity, history.Count);
        }

        [Fact]
        public void Write_CoversTheRequestedFrameRangeOnlyAndInOrder()
        {
            var history = new TileHistory();
            Fill(history, 1, 400);

            string path = history.Write(_folder, "window", 100, 120, 100, 120);
            Assert.NotNull(path);

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(22, lines.Length);                    // header plus 21 frames
            Assert.StartsWith("frame,board_time_s,in_event,t00", lines[0]);

            for (int i = 1; i < lines.Length; i++)
                Assert.Equal((100 + i - 1).ToString(), lines[i].Split(',')[0]);
        }

        [Fact]
        public void Write_HasOneColumnPerTilePlusTheTwoKeys()
        {
            var history = new TileHistory();
            Fill(history, 1, 10);

            string path = history.Write(_folder, "window", 1, 10, 1, 10);
            string[] lines = File.ReadAllLines(path);

            Assert.Equal(TileGrid.TileCount + 3, lines[0].Split(',').Length);
            Assert.Equal(TileGrid.TileCount + 3, lines[1].Split(',').Length);
        }

        [Fact]
        public void Write_LeavesAnUnsampledTileEmptyRatherThanZero()
        {
            // A tile nothing was sampled from is not a dark tile. A reader averaging a column of
            // zeros would conclude the opposite of what happened.
            var history = new TileHistory();
            history.Add(Grid(1, 100, populated: 60), 0.008);

            string path = history.Write(_folder, "window", 1, 1, 1, 1);
            string[] cells = File.ReadAllLines(path)[1].Split(',');

            Assert.Equal("100.00", cells[3]);                  // a populated tile
            Assert.Equal(string.Empty, cells[3 + 63]);         // one that was never accumulated
        }

        [Fact]
        public void Write_ReturnsNullWhenNothingInRangeIsStillHeld()
        {
            var history = new TileHistory();
            Fill(history, 1, TileHistory.Capacity + 200);      // frames 1-200 have rolled out

            Assert.Null(history.Write(_folder, "gone", 1, 50, 1, 50));
            Assert.False(File.Exists(Path.Combine(_folder, "gone.csv")),
                "an empty window must not leave a file behind");
        }

        [Fact]
        public void Write_ReturnsNullOnAnEmptyHistory()
        {
            Assert.Null(new TileHistory().Write(_folder, "empty", 0, 100, 0, 100));
        }

        [Fact]
        public void Write_KeepsTheOldestFramesAfterTheRingHasWrapped()
        {
            // The preroll is the point of the whole type, and it is the part that wraps first.
            var history = new TileHistory();
            Fill(history, 1, TileHistory.Capacity + 100);

            long oldest = 101;                                 // Capacity + 100 - Capacity + 1
            string path = history.Write(_folder, "window", oldest, oldest + 4, oldest, oldest + 4);
            Assert.NotNull(path);
            Assert.Equal(6, File.ReadAllLines(path).Length);
        }

        [Fact]
        public void Write_MarksWhichFramesWereTheEvent()
        {
            // Without this the shape analysis measures onset across the preroll too, and a tile
            // that dipped a second earlier for its own reasons counts as the event's first tile.
            var history = new TileHistory();
            Fill(history, 1, 100);

            string path = history.Write(_folder, "marked", 20, 60, 40, 45);
            string[] lines = File.ReadAllLines(path);

            Assert.Equal("in_event", lines[0].Split(',')[2]);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cells = lines[i].Split(',');
                int frame = int.Parse(cells[0]);
                string expected = frame >= 40 && frame <= 45 ? "1" : "0";
                Assert.Equal(expected, cells[2]);
            }
        }

        [Fact]
        public void Clear_DropsEverything()
        {
            var history = new TileHistory();
            Fill(history, 1, 50);
            history.Clear();
            Assert.Equal(0, history.Count);
            Assert.Null(history.Write(_folder, "cleared", 1, 50, 1, 50));
        }

        [Fact]
        public void Capacity_OutlastsTheLongestEventThatCanBeReported()
        {
            // The 2 s cap, plus debounce, plus the up-to-500 ms wait for the stats tick that
            // writes, plus the preroll. At 124.3 fps the whole of that has to still be in the ring.
            const double fps = 124.3;
            double held = TileHistory.Capacity / fps;
            Assert.True(held > 2.0 + 0.2 + 0.5 + 1.6, $"only {held:F1} s held");
        }
    }
}
