using System;
using System.IO;
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    /// <summary>
    /// The measurement CSV is the only record of a run that outlives it, and the numbers in it are
    /// what the detector's thresholds get chosen from. A silently malformed row would be discovered
    /// after the rig had been taken apart, so the format is pinned here.
    /// </summary>
    public class BrightnessLogTests : IDisposable
    {
        private readonly string _folder;

        public BrightnessLogTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "mfg-brightness-log-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }

        private static readonly DateTime Started = new DateTime(2026, 9, 1, 15, 30, 0, DateTimeKind.Local);

        [Fact]
        public void Start_NamesTheFileForTheMomentTheRunBegan()
        {
            using (var log = new BrightnessLog())
            {
                Assert.True(log.Start(_folder, Started));
                Assert.Equal("brightness-20260901-153000.csv", Path.GetFileName(log.Path));
                Assert.True(log.IsActive);
            }
        }

        [Fact]
        public void Start_CreatesTheFolderWhenItIsMissing()
        {
            Assert.False(Directory.Exists(_folder));
            using (var log = new BrightnessLog())
            {
                Assert.True(log.Start(_folder, Started));
            }
            Assert.True(Directory.Exists(_folder));
        }

        [Fact]
        public void Start_IsIdempotentSoATickCannotReopenTheFile()
        {
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                string first = log.Path;
                Assert.True(log.Start(_folder, Started.AddMinutes(5)));
                Assert.Equal(first, log.Path);   // not a second file, and no truncation
            }
        }

        [Fact]
        public void Append_WritesTheHeaderThenOneRowPerReading()
        {
            string path;
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                path = log.Path;
                log.Append(Started.AddSeconds(0.5), "Camera 0", 2, 10000, 308, 182, 92, 184.06, 0, true, 118.25f, 0.0f, 0.125f);
                log.Append(Started.AddSeconds(0.5), "Camera 1", 2, 5388.4, 348, 236, 92, 183.98, 3, true, 96.5f, 1.5f, 0.0f);
                Assert.Equal(2, log.Rows);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(BrightnessLog.Header, lines[0]);
            Assert.Equal(
                "2026-09-01 15:30:00.500,0.5,Camera 0,2,10000,308,182,92,184.06,0,118.25,0.000,0.125",
                lines[1]);
            Assert.Equal(
                "2026-09-01 15:30:00.500,0.5,Camera 1,2,5388,348,236,92,183.98,3,96.50,1.500,0.000",
                lines[2]);
        }

        [Fact]
        public void Append_LeavesTheLumaColumnsEmptyWhenTheMeterHadNothingToRead()
        {
            // The row is still written: a channel whose meter keeps failing must not be
            // indistinguishable from a channel that was idle.
            string path;
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                path = log.Path;
                log.Append(Started, "Camera 2", 2, 10000, 284, 208, 0, 0.0, 0, false, 0f, 0f, 0f);
            }

            string row = File.ReadAllLines(path)[1];
            Assert.EndsWith("Camera 2,2,10000,284,208,0,0.00,0,,,", row);
            Assert.Equal(12, row.Split(',').Length - 1);   // same column count as the header
        }

        [Fact]
        public void Header_AndRows_HaveTheSameNumberOfColumns()
        {
            string path;
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                path = log.Path;
                log.Append(Started, "Camera 0", 1, 0, 0, 0, 5, 60.0, 0, true, 10f, 0f, 90f);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(lines[0].Split(',').Length, lines[1].Split(',').Length);
        }

        [Fact]
        public void Append_QuotesAChannelNameContainingAComma()
        {
            string path;
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                path = log.Path;
                log.Append(Started, "Camera 0, left", 2, 10000, 308, 182, 1, 1.0, 0, true, 1f, 0f, 0f);
            }

            Assert.Contains("\"Camera 0, left\"", File.ReadAllLines(path)[1]);
        }

        [Fact]
        public void Append_DoesNothingBeforeStart()
        {
            using (var log = new BrightnessLog())
            {
                log.Append(Started, "Camera 0", 2, 10000, 308, 182, 1, 1.0, 0, true, 1f, 0f, 0f);
                Assert.Equal(0, log.Rows);
                Assert.False(log.IsActive);
                Assert.Null(log.Path);
            }
        }

        [Fact]
        public void Stop_IsSafeTwiceAndBeforeStart()
        {
            var log = new BrightnessLog();
            log.Stop();
            log.Start(_folder, Started);
            log.Stop();
            log.Stop();
            Assert.False(log.IsActive);
        }

        [Fact]
        public void Rows_AlreadyWrittenSurviveStop()
        {
            // AutoFlush plus an explicit close: a run that ends in a crash still leaves usable rows.
            string path;
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                path = log.Path;
                for (int i = 0; i < 10; i++)
                    log.Append(Started.AddSeconds(i * 0.5), "Camera 0", 2, 10000, 308, 182, i, 184.0, 0, true, 100f, 0f, 0f);
            }

            Assert.Equal(11, File.ReadAllLines(path).Length);
        }

        [Fact]
        public void Append_RecordsTheExposureSoARunNeedNotBeDatedByHowBrightItLooked()
        {
            // Three exposures were measured in one session and the log kept only the startup
            // value, so which snapshot belonged to which exposure had to be guessed from the
            // picture. The column exists to make that impossible.
            string path;
            using (var log = new BrightnessLog())
            {
                log.Start(_folder, Started);
                path = log.Path;
                log.Append(Started, "Camera 0", 2, 10000, 308, 182, 1, 99.6, 0, true, 29.2f, 0f, 0f);
                log.Append(Started.AddSeconds(0.5), "Camera 0", 2, 8000, 308, 182, 2, 124.3, 0, true, 23.4f, 0f, 0f);
                log.Append(Started.AddSeconds(1.0), "Camera 0", 2, 6000, 308, 182, 3, 165.3, 0, true, 17.5f, 0f, 0f);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal("exposure_us", lines[0].Split(',')[4]);
            Assert.Equal("10000", lines[1].Split(',')[4]);
            Assert.Equal("8000", lines[2].Split(',')[4]);
            Assert.Equal("6000", lines[3].Split(',')[4]);
        }

        [Fact]
        public void MaxRows_IsAboutNineHoursOfThreeChannelsAtTheStatsTick()
        {
            // Six rows a second: three channels, two ticks a second.
            double hours = BrightnessLog.MaxRows / 6.0 / 3600.0;
            Assert.InRange(hours, 8.0, 10.0);
        }
    }
}
