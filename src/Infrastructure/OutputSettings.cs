using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>Output resolution preset applied to both snapshots and recordings.</summary>
    public enum OutputResolution
    {
        Original,
        P1080,
        P720
    }

    /// <summary>
    /// App-wide output settings (folder + resolution), persisted to
    /// %LocalAppData%\MatroxFrameGrabber\settings.json so they survive restarts.
    /// </summary>
    public class OutputSettings : INotifyPropertyChanged
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MatroxFrameGrabber");
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static readonly string DefaultFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MatroxCapture");

        private static readonly string DefaultScratch =
            Path.Combine(SettingsDir, "rawscratch");

        private string _outputFolder = DefaultFolder;
        private OutputResolution _resolution = OutputResolution.Original;
        private string _ffmpegPath = "";
        private int _rawDurationSeconds = 10;
        private int _rawSegmentSeconds = 60;
        private string _rawScratchFolder = DefaultScratch;
        private bool _loading;   // suppresses Save() while Load() applies persisted values

        /// <summary>Auto-stop duration for lossless RAW recording, in seconds. 0 = manual stop.</summary>
        public int RawDurationSeconds
        {
            get => _rawDurationSeconds;
            set { int v = value < 0 ? 0 : value; if (_rawDurationSeconds != v) { _rawDurationSeconds = v; RaiseChanged(nameof(RawDurationSeconds)); Save(); } }
        }

        /// <summary>Length of each RAW recording segment (one .mp4 per segment), in seconds. Min 5.</summary>
        public int RawSegmentSeconds
        {
            get => _rawSegmentSeconds;
            set { int v = value < 5 ? 5 : value; if (_rawSegmentSeconds != v) { _rawSegmentSeconds = v; RaiseChanged(nameof(RawSegmentSeconds)); Save(); } }
        }

        /// <summary>
        /// Local (fast NVMe) folder for temporary RAW segment files before conversion. Kept separate
        /// from <see cref="OutputFolder"/> because RAW is far too fast for network storage; only the
        /// converted MP4s go to the (possibly NAS) output folder.
        /// </summary>
        public string RawScratchFolder
        {
            get => _rawScratchFolder;
            set { string v = string.IsNullOrWhiteSpace(value) ? DefaultScratch : value; if (_rawScratchFolder != v) { _rawScratchFolder = v; RaiseChanged(nameof(RawScratchFolder)); Save(); } }
        }

        /// <summary>Ensures the RAW scratch folder exists; returns it.</summary>
        public string EnsureScratchFolder()
        {
            Directory.CreateDirectory(_rawScratchFolder);
            return _rawScratchFolder;
        }

        /// <summary>Optional explicit path to ffmpeg.exe. Empty = auto-detect.</summary>
        public string FfmpegPath
        {
            get => _ffmpegPath;
            set { if (_ffmpegPath != value) { _ffmpegPath = value ?? ""; RaiseChanged(nameof(FfmpegPath)); Save(); } }
        }

        public string OutputFolder
        {
            get => _outputFolder;
            set
            {
                string v = string.IsNullOrWhiteSpace(value) ? DefaultFolder : value;
                if (_outputFolder != v) { _outputFolder = v; RaiseChanged(nameof(OutputFolder)); Save(); }
            }
        }

        public OutputResolution Resolution
        {
            get => _resolution;
            set { if (_resolution != value) { _resolution = value; RaiseChanged(nameof(Resolution)); Save(); } }
        }

        /// <summary>Target height in pixels for the preset (0 = keep original).</summary>
        [JsonIgnore]
        public int TargetHeight => _resolution switch
        {
            OutputResolution.P1080 => 1080,
            OutputResolution.P720 => 720,
            _ => 0
        };

        /// <summary>
        /// Uniform scale factor to apply to a source of the given height (aspect preserved,
        /// never upscales). 1.0 means "no resize" (Original, or source already smaller).
        /// </summary>
        public double ScaleFactorFor(long sourceHeight)
        {
            int target = TargetHeight;
            if (target <= 0 || sourceHeight <= 0 || sourceHeight <= target)
                return 1.0;
            return (double)target / sourceHeight;
        }

        /// <summary>Ensures the output folder exists; returns it.</summary>
        public string EnsureFolder()
        {
            Directory.CreateDirectory(_outputFolder);
            return _outputFolder;
        }

        #region Persistence

        // Plain DTO so (de)serialization never runs through the observable setters (which Save()).
        private class Dto
        {
            public string OutputFolder { get; set; }
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public OutputResolution Resolution { get; set; }
            public string FfmpegPath { get; set; }
            public int RawDurationSeconds { get; set; } = 10;
            public int RawSegmentSeconds { get; set; } = 60;
            public string RawScratchFolder { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public static OutputSettings Load()
        {
            var s = new OutputSettings { _loading = true };
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(SettingsPath), JsonOpts);
                    if (dto != null)
                    {
                        s._outputFolder = string.IsNullOrWhiteSpace(dto.OutputFolder) ? DefaultFolder : dto.OutputFolder;
                        s._resolution = dto.Resolution;
                        s._ffmpegPath = dto.FfmpegPath ?? "";
                        s._rawDurationSeconds = dto.RawDurationSeconds < 0 ? 0 : dto.RawDurationSeconds;
                        s._rawSegmentSeconds = dto.RawSegmentSeconds < 5 ? 5 : dto.RawSegmentSeconds;
                        s._rawScratchFolder = string.IsNullOrWhiteSpace(dto.RawScratchFolder) ? DefaultScratch : dto.RawScratchFolder;
                    }
                }
            }
            catch
            {
                // Corrupt/unreadable settings — keep defaults.
            }
            s._loading = false;
            return s;
        }

        public void Save()
        {
            if (_loading) return;   // don't rewrite while applying loaded values
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var dto = new Dto { OutputFolder = _outputFolder, Resolution = _resolution, FfmpegPath = _ffmpegPath, RawDurationSeconds = _rawDurationSeconds, RawSegmentSeconds = _rawSegmentSeconds, RawScratchFolder = _rawScratchFolder };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, JsonOpts));
            }
            catch
            {
                // Non-fatal: settings just won't persist this time.
            }
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaiseChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
