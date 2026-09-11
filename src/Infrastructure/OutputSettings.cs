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

        /// <summary>Acquisition slots on the board — always 4, camera present or not.</summary>
        public const int ChannelCount = 4;

        private readonly ChannelRoi[] _channelRois = new ChannelRoi[ChannelCount];
        private int _displayUpdateFps = 30;

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

        /// <summary>
        /// Per-channel analysis ROI — which pixels the anomaly metrics are computed over. Index is
        /// the channel's board slot (0-3). Defaults to <see cref="ChannelRoi.FullFrame"/>.
        /// </summary>
        public ChannelRoi GetRoi(int channelIndex) =>
            channelIndex < 0 || channelIndex >= ChannelCount
                ? ChannelRoi.FullFrame
                : _channelRois[channelIndex];

        // No RaiseChanged here: nothing binds the ROI through OutputSettings.
        public void SetRoi(int channelIndex, ChannelRoi roi)
        {
            if (channelIndex < 0 || channelIndex >= ChannelCount) return;
            _channelRois[channelIndex] = roi;
            Save();
        }

        private readonly int[] _channelDecimation = new int[ChannelCount];

        /// <summary>
        /// Per-channel on-board decimation factor (1, 2 or 4). The only lever that reduces host DMA
        /// traffic on this camera — see research.md section 8 and CLAUDE.md. Zero-initialised, so
        /// ClampDecimation turns an untouched slot into 1.
        /// </summary>
        public int GetDecimation(int channelIndex) =>
            channelIndex < 0 || channelIndex >= ChannelCount
                ? 1
                : ChannelRoi.ClampDecimation(_channelDecimation[channelIndex]);

        public void SetDecimation(int channelIndex, int factor)
        {
            if (channelIndex < 0 || channelIndex >= ChannelCount) return;
            int v = ChannelRoi.ClampDecimation(factor);
            if (_channelDecimation[channelIndex] == v) return;
            _channelDecimation[channelIndex] = v;
            Save();
        }

        private readonly AnomalyThresholds[] _channelThresholds = CreateThresholds();

        private static AnomalyThresholds[] CreateThresholds()
        {
            var all = new AnomalyThresholds[ChannelCount];
            for (int i = 0; i < ChannelCount; i++) all[i] = new AnomalyThresholds();
            return all;
        }

        /// <summary>
        /// Per-channel detection thresholds. Per channel because the cameras do not see the same
        /// thing: measured against one clip under one set of lighting, the dimmest of three
        /// channels produced nine shallow false positives where the other two produced none, and
        /// its own noise floor sat within 1.2x of the shared threshold. Raising the threshold for
        /// all three to fix one would throw away sensitivity on the two that were behaving.
        ///
        /// The instance is returned live rather than copied: the detector holds it for the length
        /// of a run and an edit has to reach it. Call <see cref="SaveThresholds"/> after changing
        /// one.
        /// </summary>
        public AnomalyThresholds GetThresholds(int channelIndex) =>
            channelIndex < 0 || channelIndex >= ChannelCount
                ? new AnomalyThresholds()
                : _channelThresholds[channelIndex];

        /// <summary>Persists the thresholds after a caller has edited one in place.</summary>
        public void SaveThresholds() => Save();

        /// <summary>
        /// Cap for the MIL display's update rate, in frames per second. 0 = uncapped.
        ///
        /// This does NOT recover acquisition frame rate — measured, see research.md section 8 —
        /// it buys back CPU. Clamped to 5..120 when non-zero so a typo cannot make the preview
        /// look frozen or remove the cap entirely.
        /// </summary>
        public int DisplayUpdateFps
        {
            get => _displayUpdateFps;
            set
            {
                int v = value <= 0 ? 0 : (value < 5 ? 5 : (value > 120 ? 120 : value));
                if (_displayUpdateFps != v) { _displayUpdateFps = v; RaiseChanged(nameof(DisplayUpdateFps)); Save(); }
            }
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
            public RoiDto[] ChannelRois { get; set; }
            public int DisplayUpdateFps { get; set; } = 30;
            public int[] ChannelDecimation { get; set; }

            // AnomalyThresholds is a plain mutable class with a parameterless constructor, so
            // unlike ChannelRoi it needs no mirror type. Using it directly also means a threshold
            // added to the detector reaches the settings file without a second edit here.
            public AnomalyThresholds[] ChannelThresholds { get; set; }
        }

        // ChannelRoi is a readonly struct with no parameterless constructor, so it cannot be
        // deserialized directly. This mirror type exists only for the settings file.
        private class RoiDto
        {
            public int OffsetX { get; set; }
            public int OffsetY { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
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
                        s._displayUpdateFps = dto.DisplayUpdateFps <= 0
                            ? 0
                            : (dto.DisplayUpdateFps < 5 ? 5 : (dto.DisplayUpdateFps > 120 ? 120 : dto.DisplayUpdateFps));

                        // A settings file written by an older build has no ROI array, and one
                        // written by hand may have the wrong length. Both degrade to full frame
                        // per channel rather than throwing.
                        if (dto.ChannelRois != null)
                        {
                            for (int i = 0; i < ChannelCount && i < dto.ChannelRois.Length; i++)
                            {
                                RoiDto r = dto.ChannelRois[i];
                                if (r == null) continue;
                                s._channelRois[i] = new ChannelRoi(r.OffsetX, r.OffsetY, r.Width, r.Height);
                            }
                        }

                        if (dto.ChannelThresholds != null)
                        {
                            // Copied field by field into the existing instance rather than
                            // assigned: a channel already holding a reference to it must see the
                            // loaded values, and a file written by an older build leaves the
                            // fields it does not carry at their defaults.
                            for (int i = 0; i < ChannelCount && i < dto.ChannelThresholds.Length; i++)
                            {
                                AnomalyThresholds loaded = dto.ChannelThresholds[i];
                                if (loaded == null) continue;
                                s._channelThresholds[i].CopyFrom(loaded);
                            }
                        }

                        if (dto.ChannelDecimation != null)
                        {
                            for (int i = 0; i < ChannelCount && i < dto.ChannelDecimation.Length; i++)
                                s._channelDecimation[i] = ChannelRoi.ClampDecimation(dto.ChannelDecimation[i]);
                        }
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
                var rois = new RoiDto[ChannelCount];
                for (int i = 0; i < ChannelCount; i++)
                {
                    rois[i] = new RoiDto
                    {
                        OffsetX = _channelRois[i].OffsetX,
                        OffsetY = _channelRois[i].OffsetY,
                        Width = _channelRois[i].Width,
                        Height = _channelRois[i].Height
                    };
                }
                var dto = new Dto
                {
                    OutputFolder = _outputFolder,
                    Resolution = _resolution,
                    FfmpegPath = _ffmpegPath,
                    RawDurationSeconds = _rawDurationSeconds,
                    RawSegmentSeconds = _rawSegmentSeconds,
                    RawScratchFolder = _rawScratchFolder,
                    ChannelRois = rois,
                    DisplayUpdateFps = _displayUpdateFps,
                    ChannelDecimation = (int[])_channelDecimation.Clone(),
                    ChannelThresholds = _channelThresholds
                };
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
