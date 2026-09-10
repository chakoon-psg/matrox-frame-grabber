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

        /// <summary>Acquisition slots on the board — always 4, camera present or not.</summary>
        public const int ChannelCount = 4;

        private readonly ChannelRoi[] _channelRois = new ChannelRoi[ChannelCount];
        private int _displayUpdateFps = 30;
        private VideoSinkPreference _videoSink = VideoSinkPreference.Auto;
        private int _recordingRateFps = DefaultRecordingRateFps;   // 0 = every frame
        private double _anomalyClipSeconds = DefaultAnomalyClipSeconds;
        private string _segmentFolder = DefaultSegmentFolder;

        private string _outputFolder = DefaultFolder;
        private OutputResolution _resolution = OutputResolution.Original;
        private string _ffmpegPath = "";
        private bool _loading;   // suppresses Save() while Load() applies persisted values
        private bool _migrated;  // set when Load() converted an older schema, so it is written once

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

        private readonly DetectionSettings[] _channelDetection = CreateDetection();

        private static DetectionSettings[] CreateDetection()
        {
            var a = new DetectionSettings[ChannelCount];
            for (int i = 0; i < a.Length; i++)
                a[i] = new DetectionSettings();
            return a;
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
        public DetectionSettings GetDetection(int channelIndex) =>
            channelIndex < 0 || channelIndex >= ChannelCount
                ? new DetectionSettings()
                : _channelDetection[channelIndex];

        /// <summary>Persists the settings after a caller has edited one in place.</summary>
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

        /// <summary>
        /// Where the rolling segments go, which is not where the keepers go.
        ///
        /// Local by default because the ring is written and deleted continuously while the output
        /// folder may be a network share: a latency spike there becomes FramesSkipped, and a
        /// skipped frame is a hole in the window a clip is cut from. The RAW recording this project
        /// removed kept a scratch folder for the same reason.
        ///
        /// The clips and the stills themselves go to the output folder - those are the keepers.
        /// </summary>
        public string SegmentFolder
        {
            get => _segmentFolder;
            set
            {
                string v = string.IsNullOrWhiteSpace(value) ? DefaultSegmentFolder : value;
                if (_segmentFolder != v)
                { _segmentFolder = v; RaiseChanged(nameof(SegmentFolder)); Save(); }
            }
        }

        private static readonly string DefaultSegmentFolder = Path.Combine(SettingsDir, "segments");

        /// <summary>Ensures the segment folder exists; returns it.</summary>
        public string EnsureSegmentFolder()
        {
            Directory.CreateDirectory(_segmentFolder);
            return _segmentFolder;
        }

        /// <summary>
        /// Seconds kept either side of an anomaly, as its own clip. 0 turns the event tier off.
        ///
        /// One number, and the ring's retention is derived from it rather than set separately: a
        /// ring shorter than the window makes clips quietly short, and there is no combination of
        /// the two worth offering that the arithmetic cannot produce.
        /// </summary>
        public double AnomalyClipSeconds
        {
            get => _anomalyClipSeconds;
            set
            {
                double v = value <= 0.0 ? 0.0 : (value < 1.0 ? 1.0 : (value > 60.0 ? 60.0 : value));
                if (Math.Abs(_anomalyClipSeconds - v) > 1e-9)
                { _anomalyClipSeconds = v; RaiseChanged(nameof(AnomalyClipSeconds)); Save(); }
            }
        }

        /// <summary>Default seconds either side. Five was the figure the design was measured against.</summary>
        public const double DefaultAnomalyClipSeconds = 5.0;

        /// <summary>
        /// Seconds per segment file. Short because a file the muxer is still writing cannot be
        /// read, so this is how long a clip waits after its window closes.
        /// </summary>
        public const double SegmentSeconds = 2.0;

        /// <summary>
        /// Frames per second to record at. 0 records every frame, which is what this app did before
        /// the setting existed.
        ///
        /// A wanted rate, not the rate written: it becomes a divisor, and a quarter of 124.316 fps
        /// is 31.079. Declaring the 30 that was asked for would make the file 3.6% slow - the same
        /// class of error as taking the camera's requested rate instead of its deliverable one,
        /// which once made a 120 s recording read as 81 s.
        ///
        /// Clamped to 0 or 5..1000: a rate of one or two frames a second is not a recording of a
        /// screen fault, and a typo must not produce one.
        /// </summary>
        /// <summary>
        /// The rate a fresh install records at. Thirty because that is the requirement - the
        /// full-rate tier is for the event clips, which are cut from their own files.
        /// </summary>
        public const int DefaultRecordingRateFps = 30;

        public int RecordingRateFps
        {
            get => _recordingRateFps;
            set
            {
                int v = value <= 0 ? 0 : (value < 5 ? 5 : (value > 1000 ? 1000 : value));
                if (_recordingRateFps != v) { _recordingRateFps = v; RaiseChanged(nameof(RecordingRateFps)); Save(); }
            }
        }

        /// <summary>
        /// Which backend records. Auto uses MIL when it is known to work here and ffmpeg otherwise;
        /// an explicit choice is honoured with no fallback, which is what makes a delivered MIL sink
        /// testable - see VideoSinkPolicy.
        /// </summary>
        public VideoSinkPreference VideoSink
        {
            get => _videoSink;
            set { if (_videoSink != value) { _videoSink = value; RaiseChanged(nameof(VideoSink)); Save(); } }
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
            public RoiDto[] ChannelRois { get; set; }
            public int DisplayUpdateFps { get; set; } = 30;

            // A name, not the enum's number: an unrecognised string falls back to Auto, where an
            // out-of-range index would select a backend nobody asked for.
            public string VideoSink { get; set; }
            // Nullable so an absent key means "never chosen" and takes the default, where a
            // plain int would read as 0 and silently mean "every frame".
            public int? RecordingRateFps { get; set; }
            public double? AnomalyClipSeconds { get; set; }
            public string SegmentFolder { get; set; }
            public int[] ChannelDecimation { get; set; }

            // AnomalyThresholds is a plain mutable class with a parameterless constructor, so
            // unlike ChannelRoi it needs no mirror type. Using it directly also means a threshold
            // added to the detector reaches the settings file without a second edit here.
            /// <summary>
            /// The old flat, frame-based shape. Read only to migrate from - see
            /// DetectionSettings.FromLegacy - and not written any more, so the first save after an
            /// upgrade replaces it with ChannelDetection.
            /// </summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public AnomalyThresholds[] ChannelThresholds { get; set; }

            public DetectionSettings[] ChannelDetection { get; set; }
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
                        s._segmentFolder = string.IsNullOrWhiteSpace(dto.SegmentFolder)
                            ? DefaultSegmentFolder : dto.SegmentFolder;

                        double around = dto.AnomalyClipSeconds ?? DefaultAnomalyClipSeconds;
                        s._anomalyClipSeconds = around <= 0.0
                            ? 0.0
                            : (around < 1.0 ? 1.0 : (around > 60.0 ? 60.0 : around));

                        int wanted = dto.RecordingRateFps ?? DefaultRecordingRateFps;
                        s._recordingRateFps = wanted <= 0
                            ? 0
                            : (wanted < 5 ? 5 : (wanted > 1000 ? 1000 : wanted));
                        s._videoSink =
                            Enum.TryParse(dto.VideoSink, ignoreCase: true, out VideoSinkPreference pref)
                                ? pref : VideoSinkPreference.Auto;
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

                        // Copied field by field into the existing instance rather than
                        // assigned: a channel already holding a reference to it must see the
                        // loaded values, and a file written by an older build leaves the fields it
                        // does not carry at their defaults.
                        if (dto.ChannelDetection != null)
                        {
                            for (int i = 0; i < ChannelCount && i < dto.ChannelDetection.Length; i++)
                            {
                                DetectionSettings loaded = dto.ChannelDetection[i];
                                if (loaded == null) continue;
                                s._channelDetection[i].CopyFrom(loaded);
                            }
                        }
                        else if (dto.ChannelThresholds != null)
                        {
                            // The old shape. Migrated rather than ignored, because ignoring it puts
                            // every channel back on the default depth with nothing on screen to say
                            // so - the detector would keep running and only the numbers would
                            // change. Logged for the same reason.
                            for (int i = 0; i < ChannelCount && i < dto.ChannelThresholds.Length; i++)
                            {
                                AnomalyThresholds legacy = dto.ChannelThresholds[i];
                                if (legacy == null) continue;
                                s._channelDetection[i].CopyFrom(DetectionSettings.FromLegacy(legacy));
                                s._migrated = true;
                                MilErrorLog.Note(
                                    $"settings: channel {i} detection migrated from the frame-based "
                                  + $"schema - depth {legacy.Depth:0.###} kept, times converted at "
                                  + $"{DetectionSettings.LegacyFrameRate:F3} fps, "
                                  + $"{AnomalyKind.Dropout} enabled and the other kinds off");
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

            // Written out here rather than left for the next edit: otherwise the file keeps the
            // old key and every start migrates again, which is harmless but means the conversion
            // never actually happens and the log says it did.
            if (s._migrated)
            {
                s.Save();
                MilErrorLog.Note("settings: rewritten in the per-kind schema");
            }
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
                    ChannelRois = rois,
                    DisplayUpdateFps = _displayUpdateFps,
                    VideoSink = _videoSink.ToString(),
                    RecordingRateFps = _recordingRateFps,
                    AnomalyClipSeconds = _anomalyClipSeconds,
                    SegmentFolder = _segmentFolder,
                    ChannelDecimation = (int[])_channelDecimation.Clone(),
                    ChannelDetection = _channelDetection
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
