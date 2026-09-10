using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil.Video;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Encapsulates all MIL resources and live-acquisition logic for a single camera
    /// (one digitizer on the Rapixo CXP board): a WPF display, a display buffer, a ring
    /// of grab buffers, and an MdigProcess acquisition loop whose hook performs per-frame
    /// processing and updates the display buffer.
    ///
    /// Also exposes GenICam camera control (exposure, trigger, DCF, feature browser).
    /// Every feature access is guarded by a presence check and try/catch(MILException),
    /// so cameras that lack a given SFNC feature simply disable that control.
    /// </summary>
    public class CameraChannel : INotifyPropertyChanged
    {
        #region Constants

        // Grab buffers use non-paged (DMA) memory, a limited pool shared by all cameras.
        // We request this many but tolerate getting fewer (see the resilient loop below).
        private const int REQUESTED_GRAB_BUFFERS = 4;
        private const int MIN_USABLE_GRAB_BUFFERS = 2;

        private const int DEFAULT_SIZE_X = 640;
        private const int DEFAULT_SIZE_Y = 480;
        private const int DEFAULT_SIZE_BAND = 1;

        // GenICam SFNC feature names.
        private const string F_EXPOSURE_TIME = "ExposureTime";
        private const string F_EXPOSURE_AUTO = "ExposureAuto";
        private const string F_TRIGGER_SELECTOR = "TriggerSelector";
        private const string F_TRIGGER_MODE = "TriggerMode";
        private const string F_TRIGGER_SOURCE = "TriggerSource";
        private const string F_TRIGGER_SOFTWARE = "TriggerSoftware";
        private const string F_ACQ_RATE = "AcquisitionFrameRate";
        /// <summary>Read-only: the rate the camera says the current settings allow.</summary>
        private const string F_RESULTING_RATE = "ResultingFrameRate";
        private const string F_ACQ_RATE_ENABLE = "AcquisitionFrameRateEnable";
        private const string F_BALANCE_WHITE_AUTO = "BalanceWhiteAuto";
        private const string F_BALANCE_RATIO_SELECTOR = "BalanceRatioSelector";
        private const string F_BALANCE_RATIO = "BalanceRatio";
        private const string F_DECIM_H = "DecimationHorizontal";
        private const string F_DECIM_V = "DecimationVertical";

        // Geometry nodes. This camera accepts writes to these and ignores them (see CLAUDE.md), so
        // they are named here only for the access-mode probe below — nothing writes them.
        private const string F_WIDTH = "Width";
        private const string F_HEIGHT = "Height";
        private const string F_OFFSET_X = "OffsetX";
        private const string F_OFFSET_Y = "OffsetY";

        #endregion

        #region Hook data

        private class ChannelHookData
        {
            public CameraChannel Owner;
            public MIL_ID DisplayBuffer;
            public long FrameCount;

            /// <summary>Board timestamp of the first and most recent frame of this grab, in seconds.</summary>
            public double FirstTimeStampSec;
            public double LastTimeStampSec;
        }

        #endregion

        #region Private members

        private readonly int _index;

        private MIL_ID _sysId = MIL.M_NULL;   // shared, owned by MilApplicationManager
        private MIL_ID _digId = MIL.M_NULL;
        private MIL_ID _dispId = MIL.M_NULL;
        private MIL_ID _dispBufId = MIL.M_NULL;
        private MIL_ID _graId = MIL.M_NULL;
        private readonly BrightnessMeter _brightness = new BrightnessMeter();
        private double _exposureUs;

        // Detection. The reducer and the grid are reused frame after frame -- nothing on the
        // acquisition path allocates. The detector is per-run, created in StartGrab.
        private readonly TileReducer _reducer = new TileReducer();
        private readonly TileGrid _grid = new TileGrid();

        // Recent tile grids, so a confirmed event can be written out with the frames that led to
        // it. See TileHistory for why the frames themselves cannot be kept.
        private readonly TileHistory _history = new TileHistory();
        private int _eventWindowsWritten;
        private int _rejectedWindowsWritten;
        private long _rejectedSeen;
        private AnomalyDetector _detector;

        // Events cross from the acquisition thread to the stats tick through this queue. Raising
        // them from the hook would put the grab behind whatever a UI handler decides to do.
        private readonly ConcurrentQueue<AnomalyEvent> _anomalies = new ConcurrentQueue<AnomalyEvent>();
        private long _anomalyCount;
        private AnomalyEvent _lastAnomaly;
        private bool _hasLastAnomaly;
        private readonly List<MIL_ID> _grabBuffers = new List<MIL_ID>();

        private bool _cameraAvailable;
        private string _dcfName = "M_DEFAULT";

        private ChannelHookData _hookData;
        private GCHandle _hookHandle;
        private MIL_DIG_HOOK_FUNCTION_PTR _hookDelegate;
        private bool _isGrabbing;

        private double _frameRate;

        // Feature-bound backing fields.
        private bool _supportsExposure;
        private bool _supportsExposureAuto;
        private bool _supportsTrigger;
        private bool _supportsAcqRate;
        private bool _supportsAcqRateEnable;
        private bool _acqRateEnabled;
        private string _acqRateInput = "";
        private double _acqRateMax;
        private string _exposureInput = "";
        private double _exposureMin;
        private double _exposureMax;
        private bool _exposureAuto;
        private bool _triggerOn;
        private List<string> _triggerSources = new List<string>();
        private string _selectedTriggerSource;
        private bool _supportsWhiteBalance;
        private bool _whiteBalanceAuto;
        private string _redRatioInput = "";
        private string _blueRatioInput = "";
        private string _cameraInfo = "";

        // Output naming + recording (delegated to an IVideoSink).
        private string _outputName;
        private IVideoSink _recording;
        private FfmpegVideoSink _ffmpegSink;   // the same object when ffmpeg is the backend, for its status line
        private StillRing _stills;
        private long _stillProbeKept;
        private bool _stillsExported;

        private long _framesMissed;
        private long _missedAtGrabStart;   // cumulative counter when this grab started

        // GenICam SFNC feature access (its Digitizer is updated on each (re)allocation).
        private readonly GenICamFeatures _features = new GenICamFeatures();

        // On-board decimation (reduces host DMA traffic — research.md section 8). Cropping is
        // refused by this camera; decimation is the lever instead (see CLAUDE.md).
        private int _decimation = 1;

        // Analysis ROI (software-only: which pixels the anomaly metrics read, not a camera
        // setting — see AnalysisRoi below).
        private ChannelRoi _analysisRoi = ChannelRoi.FullFrame;
        private string _roiInX = "0", _roiInY = "0", _roiInW = "0", _roiInH = "0";

        // Display-copy decimation. Time-based rather than every-Nth-frame: channels can grab at
        // very different rates (a long exposure caps one camera at 10 fps while its neighbours
        // run at 184), and a fixed divider would give each of them a different display rate.
        private readonly Stopwatch _dispClock = Stopwatch.StartNew();
        private long _lastDispCopyMs;

        // Camera-disconnect detection (polled in RefreshStats; 2 strikes to avoid false positives).
        private bool _cameraLost;
        private int _lostPolls;

        #endregion

        public CameraChannel(int index)
        {
            _index = index;
            _outputName = $"Camera {_index}";

            // StartGrab throws on MIL failure, so the command binds to the non-throwing wrapper
            // instead — an unhandled MILException on the UI thread would take the app down.
            // Start is pointless while already grabbing and Stop while stopped — and the buttons
            // must track that, because Start/Stop All changes it without touching the pane.
            StartCommand = new RelayCommand(() => TryStartGrab(), () => CameraPresent && !IsGrabbing);
            StopCommand = new RelayCommand(StopGrab, () => CameraPresent && IsGrabbing);
            FitCommand = new RelayCommand(FitToWindow, () => CameraPresent);
            OneToOneCommand = new RelayCommand(ZoomActual, () => CameraPresent);
        }

        /// <summary>
        /// Re-queries every command's CanExecute so bound buttons follow <see cref="CameraPresent"/>.
        /// Call wherever the camera appears or disappears (allocate / free / DCF reload).
        /// </summary>
        private void RaiseCommandStates()
        {
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            FitCommand.RaiseCanExecuteChanged();
            OneToOneCommand.RaiseCanExecuteChanged();
        }

        // View/acquisition commands for the pane toolbar (dialog-free actions).
        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand FitCommand { get; }
        public RelayCommand OneToOneCommand { get; }

        #region Basic properties (bound in XAML)

        public string Name => $"Camera {_index}";

        /// <summary>Fixed physical-port tag (CAM0..CAM3), shown in the pane header for field reference.</summary>
        public string PortTag => $"CAM{_index}";

        public MIL_ID DisplayId => _dispId;

        /// <summary>True while a recording is active (drives the pane banner).</summary>
        public bool RecordingActive => IsRecording;

        /// <summary>Prominent banner text shown over the live view while recording (mode + timer + missed/drops).</summary>
        public string RecordingBannerText
        {
            get
            {
                if (IsRecording)
                    return $"● 라이브 녹화 중 (H.264, 고fps 시 프레임 드랍){_ffmpegSink?.StatusSuffix()}";
                return "";
            }
        }
        public bool CameraPresent => _digId != MIL.M_NULL;
        public bool IsGrabbing => _isGrabbing;
        public long FrameCount => _hookData?.FrameCount ?? 0;
        public double FrameRate => _frameRate;

        /// <summary>Frames the board dropped for want of host bandwidth. Non-zero means the
        /// payload does not fit — see research.md section 8.</summary>
        public long FramesMissed => _framesMissed;

        /// <summary>Bytes one grabbed frame occupies (what actually crosses the bus). 0 when idle.</summary>
        public long BytesPerFrame
        {
            get
            {
                if (_grabBuffers.Count == 0 || _grabBuffers[0] == MIL.M_NULL) return 0;
                try
                {
                    return MIL.MbufInquire(_grabBuffers[0], MIL.M_SIZE_X, MIL.M_NULL)
                         * MIL.MbufInquire(_grabBuffers[0], MIL.M_SIZE_Y, MIL.M_NULL)
                         * MIL.MbufInquire(_grabBuffers[0], MIL.M_SIZE_BAND, MIL.M_NULL);
                }
                catch (MILException) { return 0; }
            }
        }

        /// <summary>This channel's brightness readings, appended on each stats tick.</summary>
        public BrightnessHistory Brightness => _brightness.History;

        /// <summary>
        /// Takes one brightness reading right now and returns it, instead of waiting for the stats
        /// tick. Only the PWM sweep uses this: the sweep needs the spread of readings at each
        /// exposure, and thirty samples in fifteen seconds understates a spread that hundreds
        /// would show. Returns false when there is nothing to read.
        ///
        /// This appends to the same history the graph draws, which is why it is not on the normal
        /// path — during an unattended sweep there is no graph to disturb.
        /// </summary>
        public bool TrySampleBrightnessNow(out BrightnessSample sample)
        {
            // The return value, not the history count: once the ring buffer is full the count stops
            // moving, and Latest would hand back the previous reading — the sweep would then
            // average a value it never measured.
            if (!_brightness.Sample(_dispBufId, _analysisRoi))
            {
                sample = default;
                return false;
            }

            sample = _brightness.History.Latest;
            return true;
        }

        /// <summary>
        /// The camera's own <c>ResultingFrameRate</c> — what it says it can deliver at the current
        /// settings. Worth more than our own 1/(exposure + 45 us) estimate for two reasons: it
        /// confirms an exposure write actually took (the rate moves with it, and this camera has
        /// form for accepting writes it then ignores), and it is the only direct answer to the rate
        /// ceiling at the current decimation — the 184 in the model name is a full-resolution
        /// figure and says nothing about decimated readout.
        /// </summary>
        public bool TryGetResultingFps(out double fps) =>
            TryGetFeatureDouble(MIL.M_FEATURE_VALUE, F_RESULTING_RATE, out fps);

        /// <summary>Sets the exposure directly, for the unattended sweep. Reads back afterwards.</summary>
        public bool SetExposureUs(double us)
        {
            if (!_supportsExposure)
                return false;

            return WriteExposureUs(us);
        }

        /// <summary>
        /// Writes the exposure, reads it back, and logs both numbers. The single place either entry
        /// point goes through, so a measurement can always be traced to the exposure it ran at:
        /// the startup line alone left mid-session changes unrecorded, which meant working out
        /// afterwards which exposure a snapshot belonged to from how bright it came out.
        /// </summary>
        private bool WriteExposureUs(double us)
        {
            bool ok = SetFeatureDouble(F_EXPOSURE_TIME, us);
            RefreshExposureReadback();
            // The read-back value, not the requested one, and logged even when the write was
            // refused: a run whose exposure silently stayed put is what this has to reveal.
            MilErrorLog.Note(
                $"{Name}: exposure asked {us:0.##} us, camera reports {_exposureUs:0.##} us" +
                (_exposureUs > 0 ? $" (=> {1e6 / (_exposureUs + InterFrameOverheadUs):0.#} fps max)" : string.Empty));

            // The exposure is what bounds the frame rate, so it is also what every millisecond
            // threshold resolves against. A 4000 us exposure doubles the rate and would otherwise
            // leave a debounce meaning half as long as it says.
            RefreshDetectionFps();
            return ok;
        }

        /// <summary>
        /// Readout overhead added to the exposure to get the camera's frame period. Measured at
        /// 45 us against the camera's own ResultingFrameRate; see PwmSweep, which uses the same
        /// figure.
        /// </summary>
        private const double InterFrameOverheadUs = 45.0;

        /// <summary>Wall time the last brightness reading took, for the tick-budget check.</summary>
        public double LastBrightnessSampleMs => _brightness.LastSampleMs;

        /// <summary>Consecutive brightness-measurement failures; non-zero means the strip is blank for a reason.</summary>
        public int BrightnessFailures => _brightness.ConsecutiveFailures;

        /// <summary>
        /// Whether this channel measures brightness on the stats tick. Off by default; MainViewModel
        /// turns it on for the session, because the strip it feeds is always on screen. Kept as an
        /// explicit switch rather than folded away: the acceptance test for this feature is a
        /// comparison of frames missed with measurement on and off.
        /// </summary>
        public bool BrightnessEnabled { get; set; }

        /// <summary>
        /// Whether this channel reduces frames and judges them. Off by default and switched on for
        /// the session by MainViewModel, kept as an explicit flag for the same reason
        /// <see cref="BrightnessEnabled"/> is: the acceptance test for putting the reduction on the
        /// acquisition path is a comparison of frames missed with it on and off.
        /// </summary>
        public bool DetectionEnabled { get; set; }

        /// <summary>
        /// The numbers this channel judges by, held in the settings file so they survive a restart
        /// and can differ between channels without a rebuild. Per channel because the cameras do
        /// not see the same thing: measured against one clip, the dimmest of three produced nine
        /// shallow false positives where the other two produced none.
        ///
        /// Falls back to a private instance only when there are no settings yet, which happens
        /// during construction before <see cref="Output"/> is attached.
        /// </summary>
        public DetectionSettings Detection =>
            Output?.GetDetection(_index) ?? _fallbackDetection;

        private readonly DetectionSettings _fallbackDetection = new DetectionSettings();

        /// <summary>
        /// The settings resolved into the frame counts the detector counts in, at the rate this
        /// camera is actually running.
        ///
        /// Stored as milliseconds and resolved here because a frame count means something else at
        /// every other rate: a debounce of 20 frames is 161 ms at 124.316 fps and 667 ms at 30.
        /// </summary>
        public AnomalyThresholds DetectionThresholds =>
            Detection.Resolve(AnomalyKind.Dropout, _detectionFps);

        /// <summary>The rate the thresholds above were resolved against.</summary>
        public double DetectionFps => _detectionFps;

        /// <summary>
        /// The five anomaly kinds as the settings window lists them, four of them not implemented.
        /// Built once: the rows are bound to and hold no state of their own.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<DetectionKindRow> DetectionKinds =>
            _detectionKinds ??= DetectionKindRow.BuildFor(this);

        private System.Collections.Generic.IReadOnlyList<DetectionKindRow> _detectionKinds;

        /// <summary>
        /// The false-positive budget and the share each running detector gets.
        ///
        /// Both, because the split is invisible otherwise: five detectors each allowed one an hour
        /// is five an hour, and the number an operator was told to expect is the total.
        /// </summary>
        public string BudgetText
        {
            get
            {
                DetectionSettings d = Detection;
                int n = d.EnabledCount;
                // Phrased without a plural: "1 false positives/hour" is what the obvious wording
                // produces, and the number is often exactly one.
                return n <= 1
                    ? $"{d.FalsePositiveBudgetPerHour:0.00} per hour"
                    : $"{d.FalsePositiveBudgetPerHour:0.00} per hour over {n} kinds "
                    + $"= {d.BudgetPerEnabledKind:0.000} each";
            }
        }

        /// <summary>Persists a change made through one of the kind rows, and refreshes what shows it.</summary>
        internal void SaveDetection()
        {
            Output?.SaveThresholds();
            RaisePropertyChanged(nameof(BudgetText));
            RaisePropertyChanged(nameof(DetectionHint));
        }

        private double _detectionFps = DetectionSettings.LegacyFrameRate;

        /// <summary>
        /// Re-reads the rate the millisecond thresholds resolve against.
        ///
        /// The camera's own ResultingFrameRate first, for the same reason recording prefers it: the
        /// measured rate does not exist until the stats tick, and the configured
        /// AcquisitionFrameRate answers 184 where the exposure allows 124.316. Falling back to the
        /// rate the values were authored at reproduces the old frame-based behaviour exactly, which
        /// is the right thing when the rate cannot be established at all.
        /// </summary>
        private void RefreshDetectionFps()
        {
            double resolved =
                TryGetResultingFps(out double r) && r > 1.0 ? r
                : (_frameRate > 1.0 ? _frameRate : DetectionSettings.LegacyFrameRate);

            if (Math.Abs(resolved - _detectionFps) < 0.001) return;
            _detectionFps = resolved;
            MilErrorLog.Note($"{Name}: detection thresholds resolve at {_detectionFps:F3} fps");
            RaisePropertyChanged(nameof(DetectionHint));
            RaisePropertyChanged(nameof(CalibrationText));
        }

        /// <summary>Editable copies of the two thresholds an operator tunes, as typed.</summary>
        public string DepthInput
        {
            get => _depthInput ??= DetectionThresholds.Depth.ToString("0.###", CultureInfo.InvariantCulture);
            set { _depthInput = value; RaisePropertyChanged(nameof(DepthInput)); }
        }

        public string CoherenceInput
        {
            get => _coherenceInput ??= DetectionThresholds.Coherence.ToString("0.###", CultureInfo.InvariantCulture);
            set { _coherenceInput = value; RaisePropertyChanged(nameof(CoherenceInput)); }
        }

        private string _depthInput;
        private string _coherenceInput;

        /// <summary>
        /// The thresholds that are not on the pane, in the units they actually act in. Frames are
        /// what the detector counts, but nobody reasons in frames at 124 fps -- and the same frame
        /// count means a different duration at a different exposure, which is exactly the sort of
        /// thing that goes unnoticed.
        /// </summary>
        public string DetectionHint
        {
            get
            {
                AnomalyThresholds t = DetectionThresholds;
                double ms = _frameRate > 0 ? 1000.0 / _frameRate : 0.0;
                return ms > 0
                    ? $"debounce {t.DebounceFrames}f ({t.DebounceFrames * ms:0} ms), cap {t.MaxEventFrames}f ({t.MaxEventFrames * ms / 1000.0:0.0} s)"
                    : $"debounce {t.DebounceFrames}f, cap {t.MaxEventFrames}f";
            }
        }

        /// <summary>
        /// Applies the typed thresholds to this channel and saves them. Returns false when either
        /// box does not parse, leaving both alone -- a half-applied pair is worse than neither.
        ///
        /// Takes effect on the next run, not this one: the detector is created per grab and holds
        /// the instance, so an edit mid-grab would change the rules underneath a baseline built
        /// under the old ones.
        /// </summary>
        public bool ApplyDetectionThresholds()
        {
            if (!double.TryParse(_depthInput, NumberStyles.Float, CultureInfo.InvariantCulture, out double depth) ||
                !double.TryParse(_coherenceInput, NumberStyles.Float, CultureInfo.InvariantCulture, out double coherence))
                return false;

            KindSettings k = Detection.For(AnomalyKind.Dropout);
            k.Deviation = depth;
            k.Coherence = coherence;
            k.Clamp();                     // so a typo cannot silence the detector
            Output?.SaveThresholds();

            // Show what was actually kept, not what was typed: Clamp may have moved it.
            _depthInput = null;
            _coherenceInput = null;
            RaisePropertyChanged(nameof(DepthInput));
            RaisePropertyChanged(nameof(CoherenceInput));
            RaisePropertyChanged(nameof(DetectionHint));
            foreach (DetectionKindRow row in DetectionKinds) row.Refresh();

            // Logged as resolved rather than as stored: milliseconds are what a person sets and
            // frames are what the detector counts, and the run is judged in frames.
            AnomalyThresholds resolved = DetectionThresholds;
            MilErrorLog.Note($"{Name}: {AnomalyKind.Dropout} thresholds now deviation "
                           + $"{k.Deviation:0.###}, coherence {k.Coherence:0.###}, "
                           + $"debounce {k.DebounceMs:0} ms ({resolved.DebounceFrames}f), "
                           + $"max event {k.MaxEventMs:0} ms ({resolved.MaxEventFrames}f) "
                           + $"at {_detectionFps:F3} fps");
            return true;
        }

        /// <summary>Anomalies confirmed during this run.</summary>
        public long AnomalyCount => Interlocked.Read(ref _anomalyCount);

        /// <summary>
        /// Falls that met depth and coherence but swept across the tiles instead of covering them
        /// at once - something crossing the field rather than the surface dimming. Surfaced because
        /// a gate whose rejections leave no trace cannot be told from a quiet rig.
        /// </summary>
        public long EventsRejectedForSpread => _detector?.EventsRejectedForSpread ?? _rejectedAtStop;

        // The detector is released before the run is summarised, so the count has to outlive it.
        // Without this the summary reported "0 swept and turned away" for a run whose own log
        // carried 72 of them - the one line a reader would take the run's verdict from.
        private long _rejectedAtStop;

        // What the last run would propose as a depth threshold, and the floor it measured. Kept
        // past the detector for the same reason, and because applying it is a separate act: a
        // threshold must not follow the picture on its own, or a failing panel becomes the normal
        // one -- the same reason the baseline freezes during an event.
        private DepthProposal _lastProposal;
        private double _floorAtStop;

        /// <summary>The most recent anomaly, formatted, or empty when there has been none.</summary>
        public string LastAnomalyText => _hasLastAnomaly ? _lastAnomaly.ToString() : string.Empty;

        /// <summary>Microseconds the last tile reduction took, the mean, and the worst of this run.</summary>
        public double LastReduceUs => _reducer.LastReduceUs;
        public double MeanReduceUs => _reducer.MeanReduceUs;
        public double MaxReduceUs => _reducer.MaxReduceUs;

        /// <summary>Consecutive reduction failures; non-zero means the detector is being fed nothing.</summary>
        public int ReducerFailures => _reducer.ConsecutiveFailures;

        /// <summary>Reductions attempted, and those that produced a grid. A gap is a fault.</summary>
        public long Reductions => _reducer.Reductions;
        public long GridsAccepted => _reducer.Accepted;

        /// <summary>
        /// What the detector measured on the last frame it judged. Exposed because the thresholds
        /// have to be chosen from the distribution these take on a healthy panel.
        /// </summary>
        public double DetectionDepth => _detector?.LastDepth ?? 0.0;
        public double DetectionCoherence => _detector?.LastCoherence ?? 0.0;
        public double DetectionBaseline => _detector?.Baseline ?? 0.0;

        /// <summary>
        /// The false-positive floor this run measured: the deepest fall on a frame judged normal,
        /// how many normal frames crowded the gate, and how many were turned away by coherence
        /// alone. The numbers the thresholds get chosen from.
        /// </summary>
        public double MaxCoherentNormalDepth => _detector?.MaxCoherentNormalDepth ?? 0.0;
        public double MaxNormalDepth => _detector?.MaxNormalDepth ?? 0.0;
        public long NormalFramesNearThreshold => _detector?.NormalFramesNearThreshold ?? 0;
        public long CoherenceSaves => _detector?.CoherenceSaves ?? 0;

        /// <summary>Raised on the stats tick for each anomaly confirmed since the last tick.</summary>
        public event Action<CameraChannel, AnomalyEvent> AnomalyDetected;

        /// <summary>What the last completed run would propose as this channel's depth threshold.</summary>
        public DepthProposal LastProposal => _lastProposal;

        /// <summary>Whether there is a proposal worth offering.</summary>
        public bool CanCalibrate => _lastProposal.IsUsable && !_isGrabbing;

        /// <summary>
        /// The proposal, and where the current threshold came from. Both, because the second is
        /// what says whether the first is worth acting on: a threshold with no recorded
        /// calibration is somebody's guess, and one calibrated before the lens moved is worse.
        /// </summary>
        public string CalibrationText
        {
            get
            {
                AnomalyThresholds t = DetectionThresholds;
                string from = string.IsNullOrEmpty(t.CalibratedAt)
                    ? "depth never calibrated"
                    : $"calibrated {t.CalibratedAt} ({t.CalibrationFrames} frames, floor {t.CalibrationFloor:0.####})";

                if (_isGrabbing)
                    return from;

                return _lastProposal.FramesJudged > 0
                    ? $"{from}   |   last run proposes {_lastProposal}"
                    : from;
            }
        }

        /// <summary>
        /// Takes the last run's proposal as this channel's depth threshold, and records where it
        /// came from.
        ///
        /// Deliberately a separate act from measuring it. The histogram is collected on every run
        /// because it costs nothing, but only a person can say the run was on a healthy panel --
        /// and a threshold derived from a run that was not becomes a threshold that accepts the
        /// fault as normal.
        /// </summary>
        public bool ApplyCalibration()
        {
            if (!_lastProposal.IsUsable)
                return false;

            Detection.ApplyCalibration(
                AnomalyKind.Dropout,
                _lastProposal.Depth,
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                _lastProposal.FramesJudged,
                _floorAtStop);
            Output?.SaveThresholds();

            _depthInput = null;
            RaisePropertyChanged(nameof(DepthInput));
            RaisePropertyChanged(nameof(DetectionHint));
            RaisePropertyChanged(nameof(CalibrationText));
            foreach (DetectionKindRow row in DetectionKinds) row.Refresh();

            KindSettings cal = Detection.For(AnomalyKind.Dropout);
            MilErrorLog.Note(
                $"{Name}: {AnomalyKind.Dropout} deviation calibrated to {cal.Deviation:0.###} "
              + $"at {cal.CalibratedAt} from {cal.CalibrationFrames} frames, "
              + $"floor {cal.CalibrationFloor:0.####}, "
              + $"budget {Detection.FalsePositiveBudgetPerHour:0.##}/hour "
              + $"over {Detection.EnabledCount} enabled kind(s) "
              + $"= {Detection.BudgetPerEnabledKind:0.###}/hour each");
            return true;
        }

        /// <summary>Editable base name used as the snapshot/recording filename prefix.</summary>
        public string OutputName
        {
            get => _outputName;
            set
            {
                string v = string.IsNullOrWhiteSpace(value) ? $"Camera {_index}" : value;
                if (_outputName == v) return;
                _outputName = v;
                RaisePropertyChanged(nameof(OutputName));
            }
        }

        /// <summary>True while a recording is in progress for this camera.</summary>
        public bool IsRecording => _recording?.IsActive ?? false;

        /// <summary>Last recording error surfaced to the UI (null if none).</summary>
        public string LastRecordError => _recording?.LastError;

        /// <summary>Path of the file being written, or the last one written. Null before the first.</summary>
        public string RecordingFilePath =>
            _recording != null && _recording.FilePaths.Count > 0 ? _recording.FilePaths[0] : null;

        /// <summary>What is doing the encoding, or why nothing can. Shown on the Rec button.</summary>
        public string RecordBackend { get; private set; } = "not probed";

        /// <summary>Whether recording is possible (ffmpeg available).</summary>
        public bool CanRecord { get; private set; }

        /// <summary>Shared app-wide output settings (folder + resolution). Set by the manager.</summary>
        public OutputSettings Output { get; set; }

        public string DcfName
        {
            get => _dcfName;
            private set { _dcfName = value; RaisePropertyChanged(nameof(DcfName)); RaisePropertyChanged(nameof(StatusText)); }
        }

        public string StatusText
        {
            get
            {
                if (!CameraPresent)
                    return "No camera";
                if (_cameraLost)
                    return "⚠ Camera disconnected — press Stop";
                string rec = _ffmpegSink?.StatusSuffix() ?? "";
                if (_isGrabbing)
                    return $"Grabbing  {FrameRate:F1} fps  ({FrameCount} frames)"
                         + (_framesMissed > 0 ? $"  ⚠ {_framesMissed} missed" : "")
                         + rec;
                if (_grabBuffers.Count < MIN_USABLE_GRAB_BUFFERS)
                    return $"Low memory: only {_grabBuffers.Count} grab buffer(s)";
                return $"Ready  ({_grabBuffers.Count} buffers)";
            }
        }

        #endregion

        #region Feature-bound properties (bound in XAML)

        public bool SupportsExposure { get => _supportsExposure; private set { _supportsExposure = value; RaisePropertyChanged(nameof(SupportsExposure)); RaisePropertyChanged(nameof(CanSetExposureManually)); } }
        public bool SupportsExposureAuto { get => _supportsExposureAuto; private set { _supportsExposureAuto = value; RaisePropertyChanged(nameof(SupportsExposureAuto)); } }
        public bool SupportsTrigger { get => _supportsTrigger; private set { _supportsTrigger = value; RaisePropertyChanged(nameof(SupportsTrigger)); } }

        /// <summary>Manual exposure is editable only when the feature exists and auto-exposure is off.</summary>
        public bool CanSetExposureManually => _supportsExposure && !_exposureAuto;

        /// <summary>Exposure time in microseconds, as text for the input box.</summary>
        public string ExposureInput { get => _exposureInput; set { _exposureInput = value; RaisePropertyChanged(nameof(ExposureInput)); } }

        /// <summary>
        /// The exposure the camera reports, in microseconds; 0 when the camera has no exposure
        /// feature. Read back from the camera rather than parsed from <see cref="ExposureInput"/>:
        /// the box holds what was asked for, and this camera has form for accepting a write and
        /// then ignoring it. Recorded on every measurement row so a run's exposure never has to be
        /// inferred from how bright the picture came out.
        /// </summary>
        public double ExposureUs => _exposureUs;

        public string ExposureRangeHint =>
            _supportsExposure ? $"µs  [{_exposureMin:0}–{_exposureMax:0}]" : "µs";

        public bool ExposureAuto
        {
            get => _exposureAuto;
            set
            {
                if (_exposureAuto == value) return;
                if (SetExposureAuto(value))     // only commit if the camera accepted it
                    _exposureAuto = value;
                RaisePropertyChanged(nameof(ExposureAuto));
                RaisePropertyChanged(nameof(CanSetExposureManually));
                RefreshExposureReadback();
            }
        }

        // ----- Acquisition frame-rate cap (Acq Rate) -----

        /// <summary>True if the camera exposes AcquisitionFrameRate.</summary>
        public bool SupportsAcqRate { get => _supportsAcqRate; private set { _supportsAcqRate = value; RaisePropertyChanged(nameof(SupportsAcqRate)); RaisePropertyChanged(nameof(CanSetAcqRate)); } }

        /// <summary>True if the camera has an AcquisitionFrameRateEnable toggle (the "Limit" checkbox).</summary>
        public bool SupportsAcqRateEnable { get => _supportsAcqRateEnable; private set { _supportsAcqRateEnable = value; RaisePropertyChanged(nameof(SupportsAcqRateEnable)); } }

        /// <summary>"Limit" — enable the configured acquisition rate (off = free-run at max).</summary>
        public bool AcqRateEnabled
        {
            get => _acqRateEnabled;
            set
            {
                if (_acqRateEnabled == value) return;
                if (SetFeatureBool(F_ACQ_RATE_ENABLE, value))
                    _acqRateEnabled = value;
                RaisePropertyChanged(nameof(AcqRateEnabled));
                RaisePropertyChanged(nameof(CanSetAcqRate));
                RefreshAcqRateReadback();
            }
        }

        /// <summary>Manual rate is editable when supported and (if there's an Enable) it is on.</summary>
        public bool CanSetAcqRate => _supportsAcqRate && (!_supportsAcqRateEnable || _acqRateEnabled);

        /// <summary>Target acquisition rate (fps) as text for the input box.</summary>
        public string AcqRateInput { get => _acqRateInput; set { _acqRateInput = value; RaisePropertyChanged(nameof(AcqRateInput)); } }

        public string AcqRateHint => _supportsAcqRate ? $"fps  · max {_acqRateMax:0}" : "fps";

        // ----- Decimation (payload reduction) -----

        /// <summary>The decimation factor confirmed applied by reading it back from the camera.</summary>
        public int Decimation => _decimation;

        /// <summary>True if the camera exposes decimation. This one does; cropping it does not.</summary>
        public bool SupportsDecimation => FeatureAvailable(F_DECIM_H);

        /// <summary>
        /// Decimation factors the pane combo offers. This sits on the channel, not on
        /// MainViewModel, because the pane bound ItemsSource through
        /// {RelativeSource AncestorType=Window} and that resolved later than SelectedItem,
        /// which is a plain DataContext binding. SelectedItem was therefore applied against
        /// an empty list and dropped to null, and the combo read blank for good: the only
        /// notification for Decimation fires inside AllocateCamera, which MainWindow runs
        /// *before* InitializeComponent, so no pane exists to hear it. Binding to the
        /// channel own DataContext removes the ancestor walk and the ordering with it.
        /// </summary>
        public IReadOnlyList<int> DecimationOptions => ChannelRoi.AllowedDecimation;

        /// <summary>Effective frame size the digitizer reports, for the pane hint.</summary>
        public string DecimationHint
        {
            get
            {
                if (_digId == MIL.M_NULL) return "no camera";
                try
                {
                    MIL_INT sx = MIL.MdigInquire(_digId, MIL.M_SIZE_X, MIL.M_NULL);
                    MIL_INT sy = MIL.MdigInquire(_digId, MIL.M_SIZE_Y, MIL.M_NULL);
                    return $"{sx}×{sy}";
                }
                catch (MILException) { return "?"; }
            }
        }

        // ----- Analysis ROI (which pixels the anomaly metrics are computed over) -----

        /// <summary>
        /// The region the anomaly metrics are computed over, in coordinates of the acquired
        /// (already decimated) frame. Full frame by default.
        ///
        /// This is a software value, not a camera setting: nothing in hardware can refuse it and
        /// changing it needs no reallocation. That is the whole reason it exists — this camera
        /// ignores writes to Width/Height/OffsetX/OffsetY (see CLAUDE.md), so the rectangle moved
        /// here, where it says which pixels we look at rather than which pixels we ask for.
        /// </summary>
        public ChannelRoi AnalysisRoi => _analysisRoi;

        /// <summary>Frame size the ROI is clamped against, for the input hint.</summary>
        public string AnalysisRoiHint
        {
            get
            {
                if (!TryGetFrameSize(out int w, out int h)) return "no camera";
                return _analysisRoi.IsFullFrame ? $"full {w}×{h}" : $"of {w}×{h}";
            }
        }

        public string RoiInX { get => _roiInX; set { _roiInX = value; RaisePropertyChanged(nameof(RoiInX)); } }
        public string RoiInY { get => _roiInY; set { _roiInY = value; RaisePropertyChanged(nameof(RoiInY)); } }
        public string RoiInW { get => _roiInW; set { _roiInW = value; RaisePropertyChanged(nameof(RoiInW)); } }
        public string RoiInH { get => _roiInH; set { _roiInH = value; RaisePropertyChanged(nameof(RoiInH)); } }

        public bool TriggerOn
        {
            get => _triggerOn;
            set
            {
                if (_triggerOn == value) return;
                if (SetTriggerMode(value))
                    _triggerOn = value;
                RaisePropertyChanged(nameof(TriggerOn));
            }
        }

        public List<string> TriggerSources { get => _triggerSources; private set { _triggerSources = value; RaisePropertyChanged(nameof(TriggerSources)); } }

        public string SelectedTriggerSource
        {
            get => _selectedTriggerSource;
            set
            {
                if (_selectedTriggerSource == value) return;
                _selectedTriggerSource = value;
                if (!string.IsNullOrEmpty(value))
                    TrySetFeatureString(F_TRIGGER_SOURCE, value);
                RaisePropertyChanged(nameof(SelectedTriggerSource));
            }
        }

        /// <summary>True for color cameras that expose white-balance features.</summary>
        public bool SupportsWhiteBalance { get => _supportsWhiteBalance; private set { _supportsWhiteBalance = value; RaisePropertyChanged(nameof(SupportsWhiteBalance)); RaisePropertyChanged(nameof(CanSetBalanceManually)); } }

        /// <summary>Continuous auto white balance on/off.</summary>
        public bool WhiteBalanceAuto
        {
            get => _whiteBalanceAuto;
            set
            {
                if (_whiteBalanceAuto == value) return;
                if (SetWhiteBalanceAuto(value))
                    _whiteBalanceAuto = value;
                RaisePropertyChanged(nameof(WhiteBalanceAuto));
                RaisePropertyChanged(nameof(CanSetBalanceManually));
                RefreshWhiteBalanceReadback();
            }
        }

        /// <summary>Manual R/B ratios are editable when supported and continuous-auto is off.</summary>
        public bool CanSetBalanceManually => _supportsWhiteBalance && !_whiteBalanceAuto;

        public string RedRatioInput { get => _redRatioInput; set { _redRatioInput = value; RaisePropertyChanged(nameof(RedRatioInput)); } }
        public string BlueRatioInput { get => _blueRatioInput; set { _blueRatioInput = value; RaisePropertyChanged(nameof(BlueRatioInput)); } }

        /// <summary>Camera model/resolution summary for the header tooltip.</summary>
        public string CameraInfo { get => _cameraInfo; private set { _cameraInfo = value; RaisePropertyChanged(nameof(CameraInfo)); } }

        #endregion

        #region Allocation / cleanup

        /// <summary>
        /// Allocates the display and (if a camera is present) the digitizer and buffers.
        /// The display is always allocated so the WPF view has a DisplayId to bind to.
        /// </summary>
        public void Allocate(MIL_ID sysId, bool cameraAvailable, string dcfName)
        {
            _sysId = sysId;
            _cameraAvailable = cameraAvailable;
            _dcfName = string.IsNullOrWhiteSpace(dcfName) ? "M_DEFAULT" : dcfName;

            MIL.MdispAlloc(_sysId, MIL.M_DEFAULT, "M_DEFAULT", MIL.M_WPF, ref _dispId);
            MIL.MdispControl(_dispId, MIL.M_TITLE, Name);
            MIL.MgraAlloc(_sysId, ref _graId);
            // Which backend is decided here, once, and reported. Nothing downstream knows or
            // cares which one it got.
            _recording = VideoSinkFactory.Create(_sysId, Output, Output?.VideoSink ?? VideoSinkPreference.Auto,
                                                 out string backend);
            _ffmpegSink = _recording as FfmpegVideoSink;
            RecordBackend = backend;

            AllocateCamera();

            RaisePropertyChanged(nameof(DisplayId));
        }

        /// <summary>
        /// (Re)allocates the digitizer, display buffer, and grab-buffer ring using the current
        /// DCF. Safe to call repeatedly (e.g. after choosing a different DCF). The display id
        /// itself is preserved so the WPF binding is unaffected.
        /// </summary>
        private void AllocateCamera()
        {
            if (_cameraAvailable)
            {
                // A fixed-digitizer board (e.g. Rapixo CXP with 4 ports) reports all its
                // digitizers even when some ports have no camera. Allocating an empty port
                // raises a "camera not found" error; treat any failure as simply "no camera
                // on this port" (MIL error printing is disabled process-wide).
                try
                {
                    MIL.MdigAlloc(_sysId, MIL.M_DEV0 + _index, _dcfName, MIL.M_DEFAULT, ref _digId);
                }
                catch (MILException)
                {
                    _digId = MIL.M_NULL;
                }

                if (_digId != MIL.M_NULL &&
                    MIL.MdigInquire(_digId, MIL.M_CAMERA_PRESENT, MIL.M_NULL) == MIL.M_NO)
                {
                    MIL.MdigFree(_digId);
                    _digId = MIL.M_NULL;
                }

                if (_digId != MIL.M_NULL)
                {
                    // The feature wrapper needs the digitizer id before anything below asks it a
                    // question: Available() returns false on a null id, so a ROI write placed
                    // above this line silently does nothing. The assignment after AllocateBuffers
                    // stays, because the no-camera path must still clear a stale id.
                    _features.Digitizer = _digId;

                    // Ask the camera about the geometry nodes BEFORE anything below writes to it,
                    // so the log shows the state as found.
                    LogGeometryAccess();

                    // And the acquisition-limiting settings, so "why is this one channel slow" is
                    // answered in the log instead of being re-derived. CLAUDE.md warns that the
                    // first suspect is exposure, not the cable — this line names it.
                    MilErrorLog.Note(DumpDiagnostics());

                    // Force hardware Bayer→RGB conversion ON (color). M_BAYER_CONVERSION is a
                    // PERSISTENT board setting: once disabled (e.g. to grab raw band-1 Bayer) it
                    // stays off across grabs and app restarts, and the color pipeline then misreads
                    // the mono/raw data as a tiled/garbled image. Re-assert it here, before inquiring
                    // M_SIZE_BAND, so buffers are always 3-band color. (Softly ignored on mono
                    // cameras that have no Bayer filter.)
                    try { MIL.MdigControl(_digId, MIL.M_BAYER_CONVERSION, MIL.M_ENABLE); }
                    catch (MILException e) { MilErrorLog.Write($"{Name}: re-assert M_BAYER_CONVERSION", e); }

                    // Enable Rec only if something can encode, and keep the reason: a greyed-out
                    // button with nothing to read was the old behaviour.
                    CanRecord = VideoSinkFactory.CanRecord(Output,
                                                           Output?.VideoSink ?? VideoSinkPreference.Auto,
                                                           out string why);
                    RecordBackend = why;
                    RaisePropertyChanged(nameof(RecordBackend));
                    // Written down because the alternative is a greyed-out Rec button and a guess.
                    MilErrorLog.Note($"{Name}: recording {(CanRecord ? "via " : "unavailable - ")}{why}"
                                   + $" (preference {Output?.VideoSink ?? VideoSinkPreference.Auto})");
                    RaisePropertyChanged(nameof(CanRecord));

                    // Decimation before AllocateBuffers: the buffer sizes come from
                    // M_SIZE_X/M_SIZE_Y, which only reflect it once it is written. This is the one
                    // lever that reduces host DMA traffic on this camera — cropping is refused.
                    WriteDecimationToCamera();
                }
            }

            AllocateBuffers(REQUESTED_GRAB_BUFFERS);

            _features.Digitizer = _digId;   // may be M_NULL (no camera) — features then fail softly
            _cameraLost = false; _lostPolls = 0;
            RefreshFeatureState();

            // The analysis rectangle as actually applied, after the load-time snap. Logged because
            // a stale one is invisible until somebody notices the overlay sitting off the image.
            if (CameraPresent)
            {
                // Frame size formatted here rather than reused from DecimationHint: that one is
                // UI text and carries a multiplication sign, which the log should not (see the
                // ASCII note on MilErrorLog).
                TryGetFrameSize(out int frameW, out int frameH);
                MilErrorLog.Note($"{Name}: analysis ROI {_analysisRoi} in {frameW}x{frameH} (decim {_decimation})");

                // Before the thresholds are logged: the line below reports frame counts, and
                // they are only this camera's once the rate has been read.
                RefreshDetectionFps();

                // The thresholds as loaded, for the same reason: they now come from a file that a
                // person edits, they differ per channel on purpose, and a value that silently fell
                // back to its default would otherwise be invisible until a run reported nothing.
                AnomalyThresholds t = DetectionThresholds;
                MilErrorLog.Note($"{Name}: detection thresholds at {_detectionFps:F3} fps - "
                               + $"deviation {t.Depth:0.###}, "
                               + $"coherence {t.Coherence:0.###}, debounce {t.DebounceFrames}f, "
                               + $"max event {t.MaxEventFrames}f, baseline {t.BaselineWindow}"
                               + $"/{t.BaselineWarmupFrames}f, "
                               + $"onset spread {t.MaxOnsetSpreadFrames}f over {t.MinOnsetTiles}+ tiles; "
                               + $"kinds enabled {Detection.EnabledCount} of {AnomalyCatalog.Count}, "
                               + $"budget {Detection.BudgetPerEnabledKind:0.###}/hour each");
            }

            RaisePropertyChanged(nameof(CameraPresent));
            RaisePropertyChanged(nameof(StatusText));
            RaiseCommandStates();
        }

        /// <summary>
        /// Allocates the display buffer + grab-buffer ring sized to the digitizer's CURRENT payload
        /// (band reflects the Bayer-conversion state: 3 = color, 1 = raw Bayer). Assumes the digitizer
        /// is already allocated; also called when switching to/from raw-Bayer recording.
        /// </summary>
        private void AllocateBuffers(int grabCount)
        {
            MIL_INT sizeBand = DEFAULT_SIZE_BAND;
            MIL_INT sizeX = DEFAULT_SIZE_X;
            MIL_INT sizeY = DEFAULT_SIZE_Y;
            MIL_INT bufType = 8 + MIL.M_UNSIGNED;

            if (_digId != MIL.M_NULL)
            {
                sizeBand = MIL.MdigInquire(_digId, MIL.M_SIZE_BAND, MIL.M_NULL);
                sizeX = MIL.MdigInquire(_digId, MIL.M_SIZE_X, MIL.M_NULL);
                sizeY = MIL.MdigInquire(_digId, MIL.M_SIZE_Y, MIL.M_NULL);
                bufType = MIL.MdigInquire(_digId, MIL.M_TYPE, MIL.M_NULL);
            }

            // Display buffer (viewable + processable). The hook copies frames here, so it is
            // NOT a grab target and deliberately omits M_GRAB — that keeps it in ordinary
            // (paged) memory and preserves the scarce non-paged/DMA pool for grab buffers.
            MIL.MbufAllocColor(_sysId, sizeBand, sizeX, sizeY, bufType,
                MIL.M_IMAGE + MIL.M_DISP + MIL.M_PROC, ref _dispBufId);
            MIL.MbufClear(_dispBufId, MIL.M_COLOR_BLACK);

            MIL_INT sizeBit = MIL.MbufInquire(_dispBufId, MIL.M_SIZE_BIT, MIL.M_NULL);
            if (sizeBit > 8)
            {
                MIL.MdispControl(_dispId, MIL.M_VIEW_MODE, MIL.M_BIT_SHIFT);
                MIL.MdispControl(_dispId, MIL.M_VIEW_BIT_SHIFT, sizeBit - 8);
            }

            if (!CameraPresent)
            {
                MIL.MgraControl(_graId, MIL.M_COLOR, MIL.M_COLOR_WHITE);
                MIL.MgraText(_graId, _dispBufId, sizeX / 2 - 60, sizeY / 2, $"{Name}: no camera");
            }

            MIL.MdispSelect(_dispId, _dispBufId);

            // Let the MIL display handle interactive pan (drag) and zoom (wheel / +- keys).
            // The MILWPFDisplay control forwards mouse/keyboard/wheel input to the display.
            MIL.MdispControl(_dispId, MIL.M_MOUSE_USE, MIL.M_ENABLE);
            MIL.MdispControl(_dispId, MIL.M_KEYBOARD_USE, MIL.M_ENABLE);
            // Fit the whole image to the control initially (aspect ratio preserved). M_ONCE
            // fits one time and then leaves manual zoom/pan usable (M_ENABLE would lock them).
            MIL.MdispControl(_dispId, MIL.M_SCALE_DISPLAY, MIL.M_ONCE);
            // The display paints whatever the image does not cover, and its default is white — a
            // bright slab in a dark themed app. M_COLOR_BLACK is not exactly the panel colour, but
            // it is the one value MIL takes reliably here and it reads as part of the frame rather
            // than a hole in it.
            try { MIL.MdispControl(_dispId, MIL.M_BACKGROUND_COLOR, MIL.M_COLOR_BLACK); }
            catch (MILException e) { MilErrorLog.Write($"{Name}: set display background colour", e); }
            ApplyDisplayUpdateCap();

            // Snap on load, not only on operator input. A settings file can hold a rectangle that
            // no longer fits — one written at a different decimation factor, or edited by hand —
            // and the interactive path is the only one that used to clamp. Drawing an out-of-range
            // rectangle verbatim puts the overlay off the image, which is how the operator sees it.
            ChannelRoi stored = Output?.GetRoi(_index) ?? ChannelRoi.FullFrame;
            _analysisRoi = !stored.IsFullFrame && TryGetFrameSize(out int roiW, out int roiH)
                ? stored.Snap(roiW, roiH)
                : stored;
            SyncRoiInputs();

            if (CameraPresent)
            {
                // Allocate as many grab buffers as the non-paged pool allows. With
                // M_THROW_EXCEPTION enabled a shortfall would otherwise abort startup, so each
                // allocation is guarded and we simply stop once the pool is exhausted.
                for (int i = 0; i < grabCount; i++)
                {
                    MIL_ID buf = MIL.M_NULL;
                    try
                    {
                        MIL.MbufAllocColor(_sysId, sizeBand, sizeX, sizeY, bufType,
                            MIL.M_IMAGE + MIL.M_GRAB + MIL.M_PROC, ref buf);
                    }
                    catch (MILException)
                    {
                        buf = MIL.M_NULL;
                    }
                    if (buf == MIL.M_NULL)
                        break;
                    MIL.MbufClear(buf, 0);
                    _grabBuffers.Add(buf);
                }
            }

            // Band children live exactly as long as the ring does; see TileReducer for why they
            // are not created per frame.
            _reducer.Bind(_grabBuffers);
        }

        /// <summary>Frees the grab ring + display buffer, keeping the digitizer and display alive.</summary>
        private void FreeBuffers()
        {
            // Before the ring, not after: a band child outliving its parent is a failure this
            // codebase already documents.
            _reducer.Unbind();

            foreach (MIL_ID buf in _grabBuffers)
            {
                if (buf != MIL.M_NULL)
                    MIL.MbufFree(buf);
            }
            _grabBuffers.Clear();

            if (_dispId != MIL.M_NULL)
                MIL.MdispSelect(_dispId, MIL.M_NULL);

            _brightness.Reset();

            if (_dispBufId != MIL.M_NULL)
            {
                MIL.MbufFree(_dispBufId);
                _dispBufId = MIL.M_NULL;
            }
        }

        /// <summary>Frees the digitizer, grab buffers, and display buffer (keeps the display).</summary>
        private void FreeCamera()
        {
            StopGrab();   // also stops recording (kicks off async finalize)

            // Wait for any in-flight recording finalize before freeing MIL buffers/system:
            // finalization touches the sink's MIL buffers.
            _recording?.WaitFinalize(15000);
            _recording?.Dispose();
            _recording = null;
            _ffmpegSink = null;
            _stills?.Free();
            _stills = null;

            FreeBuffers();

            if (_digId != MIL.M_NULL)
            {
                MIL.MdigFree(_digId);
                _digId = MIL.M_NULL;
            }
            _features.Digitizer = MIL.M_NULL;

            RaisePropertyChanged(nameof(CameraPresent));
            RaiseCommandStates();
        }

        /// <summary>Frees every MIL resource owned by this channel (not the shared system).</summary>
        public void Free()
        {
            FreeCamera();

            if (_graId != MIL.M_NULL)
            {
                MIL.MgraFree(_graId);
                _graId = MIL.M_NULL;
            }

            if (_dispId != MIL.M_NULL)
            {
                MIL.MdispFree(_dispId);
                _dispId = MIL.M_NULL;
            }
        }

        #endregion

        #region Acquisition

        public void StartGrab()
        {
            if (!CameraPresent || _isGrabbing || _grabBuffers.Count == 0)
                return;

            _hookData = new ChannelHookData
            {
                Owner = this,
                DisplayBuffer = _dispBufId,
                FrameCount = 0
            };
            _hookHandle = GCHandle.Alloc(_hookData);
            _hookDelegate = new MIL_DIG_HOOK_FUNCTION_PTR(ProcessFrame);

            try
            {
                MIL.MdigProcess(_digId, _grabBuffers.ToArray(), _grabBuffers.Count,
                    MIL.M_START, MIL.M_DEFAULT, _hookDelegate, GCHandle.ToIntPtr(_hookHandle));
            }
            catch (MILException)
            {
                // Don't leak the pinned hook data / delegate if the grab failed to start.
                if (_hookHandle.IsAllocated) _hookHandle.Free();
                _hookDelegate = null;
                _hookData = null;
                throw;
            }

            // Baseline the missed-frame counter. MIL keeps it cumulative — there is a separate
            // M_PROCESS_FRAME_MISSED_RESET constant, which is why — so a second grab in the same
            // session would otherwise inherit the first one's losses and read as a regression that
            // never happened. Subtracting a baseline is correct whether or not MIL also clears the
            // counter on M_START: if it does, this is zero and the subtraction is a no-op.
            _missedAtGrabStart = 0;
            try
            {
                MIL_INT m = 0;
                MIL.MdigInquire(_digId, MIL.M_PROCESS_FRAME_MISSED, ref m);
                _missedAtGrabStart = m;
            }
            catch (MILException e) { MilErrorLog.Write($"{Name}: baseline the missed-frame counter", e); }
            _framesMissed = 0;

            // A fresh detector per run. Carrying a baseline across a stop would judge the opening
            // frames of the new run against the light of the old one, and the exposure or the
            // region may well have changed in between -- during the exposure scan both did.
            if (App.StillProbe && _stills == null && _dispBufId != MIL.M_NULL)
            {
                var ring = new StillRing();
                if (ring.Allocate(_sysId, _dispBufId, out string stillErr))
                    _stills = ring;
                else
                    MilErrorLog.Note($"{Name}: still buffers not allocated - {stillErr}");
            }
            _stillProbeKept = 0;
            _stillsExported = false;

            _detector = new AnomalyDetector(DetectionThresholds);
            _reducer.ResetCost();
            _history.Clear();
            _eventWindowsWritten = 0;
            _rejectedWindowsWritten = 0;
            _rejectedSeen = 0;
            _rejectedAtStop = 0;
            while (_anomalies.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _anomalyCount, 0);
            _hasLastAnomaly = false;

            _isGrabbing = true;
            RaisePropertyChanged(nameof(IsGrabbing));
            RaisePropertyChanged(nameof(StatusText));
            RaiseCommandStates();
        }

        /// <summary>
        /// Starts acquisition without throwing: a MIL failure is reported through
        /// <see cref="GrabFailed"/> and returned as false. Use this from UI/bulk callers —
        /// <see cref="StartGrab"/> itself still throws, so a caller that needs to undo something
        /// on failure can still catch it.
        /// </summary>
        public bool TryStartGrab()
        {
            try
            {
                StartGrab();
                return true;
            }
            catch (MILException e)
            {
                GrabFailed?.Invoke(this, e.Message);
                RaisePropertyChanged(nameof(StatusText));
                return false;
            }
        }

        public void StopGrab()
        {
            if (!_isGrabbing)
                return;

            StopRecording();   // no frames will be fed once the grab stops

            MIL.MdigProcess(_digId, _grabBuffers.ToArray(), _grabBuffers.Count,
                MIL.M_STOP, MIL.M_DEFAULT, _hookDelegate, GCHandle.ToIntPtr(_hookHandle));

            // Before Flush clears it: the floor this run measured is the reason for running it.
            if (DetectionEnabled && _detector != null)
                MilErrorLog.Note(
                    $"{Name}: depth proposal - {_detector.Propose(DetectionThresholds.FalsePositiveBudgetPerHour, _frameRate)}"
                  + $" against the current {DetectionThresholds.Depth:0.###}");

            if (DetectionEnabled && _detector != null)
                MilErrorLog.Note(
                    $"{Name}: detection floor - coherent normal depth max {_detector.MaxCoherentNormalDepth:F4} "
                  + $"at frame {_detector.MaxCoherentNormalDepthFrame} "
                  + $"vs threshold {DetectionThresholds.Depth:F2} "
                  + $"({(_detector.MaxCoherentNormalDepth > 0 ? DetectionThresholds.Depth / _detector.MaxCoherentNormalDepth : 0):F1}x margin), "
                  + $"{_detector.NormalFramesNearThreshold} past half of it; "
                  + $"any-normal depth max {_detector.MaxNormalDepth:F4}, "
                  + $"{_detector.CoherenceSaves} turned away by coherence alone; "
                  + $"{_detector.Observed} frames judged");

            // A fault still running when the grab ends would otherwise never be reported at all.
            AnomalyEvent? tail = _detector?.Flush();
            if (tail.HasValue)
                RecordAnomaly(tail.Value);

            if (_stills != null && _stills.IsAllocated)
            {
                MilErrorLog.Note($"{Name}: stills - {_stills.Copies} kept, "
                               + $"copy mean {_stills.MeanCopyUs:F0} us / max {_stills.MaxCopyUs:F0} us "
                               + $"of the {(_frameRate > 0 ? 1e6 / _frameRate : 0):F0} us frame period; "
                               + $"{_stills.Exports} PNG written, "
                               + $"max {_stills.MaxExportMs:F1} ms, total {_stills.ExportMsSum:F1} ms");
            }

            // Before releasing it: LogGrabSummary reads these, and a detector that is gone reports
            // nothing rather than what it found.
            _rejectedAtStop = _detector?.EventsRejectedForSpread ?? 0;
            _floorAtStop = _detector?.MaxCoherentNormalDepth ?? 0.0;
            if (_detector != null)
                _lastProposal = _detector.Propose(
                    DetectionThresholds.FalsePositiveBudgetPerHour, _frameRate);
            _detector = null;
            RaisePropertyChanged(nameof(CalibrationText));
            RaiseCommandStates();

            // Leave the run its own evidence. The acceptance criterion for the whole payload
            // change is that frames missed does not increase, and until now reading it meant
            // transcribing the status line by hand while the run was still going.
            LogGrabSummary();

            // A stopped channel is no longer measuring anything; keeping the old readings would
            // have the strip assert a value nobody is watching anymore.
            _brightness.Reset();

            if (_hookHandle.IsAllocated)
                _hookHandle.Free();
            _hookDelegate = null;

            _isGrabbing = false;
            RaisePropertyChanged(nameof(IsGrabbing));
            RaisePropertyChanged(nameof(StatusText));
            RaiseCommandStates();
        }

        /// <summary>Raised when a recording stops on its own (ffmpeg died); carries the error text.</summary>
        public event Action<CameraChannel, string> RecordingFailed;

        /// <summary>Raised when the camera is detected as disconnected while grabbing.</summary>
        public event Action<CameraChannel> CameraLost;

        /// <summary>Raised when acquisition could not be started; carries the MIL error text.</summary>
        public event Action<CameraChannel, string> GrabFailed;

        public void RefreshStats()
        {
            if (_isGrabbing && _digId != MIL.M_NULL)
            {
                double rate = 0.0;
                MIL.MdigInquire(_digId, MIL.M_PROCESS_FRAME_RATE, ref rate);
                _frameRate = rate;

                // Brightness is measured here, on the stats tick, and never in the grab hook:
                // anything added to MdigProcess runs inside the acquisition budget. Still gated by
                // BrightnessEnabled so the cost can be switched off when measuring frames missed.
                // Measured inside the analysis ROI, or over the whole frame when none is drawn.
                // _analysisRoi and the display buffer are in the same space — both are in pixels of
                // the acquired, already-decimated frame — so no conversion belongs here.
                if (BrightnessEnabled)
                    _brightness.Sample(_dispBufId, _analysisRoi);

                // Frames the board dropped because the host could not take them fast enough.
                // Read on every tick, not only while RAW-recording: this is the acceptance
                // criterion for the whole payload change, and an over-budget configuration loses
                // frames silently otherwise.
                MIL_INT missed = 0;
                try { MIL.MdigInquire(_digId, MIL.M_PROCESS_FRAME_MISSED, ref missed); }
                catch (MILException e) { MilErrorLog.Write($"{Name}: read missed-frame counter", e); }
                // This run's losses, not the digitizer's lifetime total (see StartGrab).
                _framesMissed = Math.Max(0, (long)missed - _missedAtGrabStart);

                StillRing stills = _stills;
                if (stills != null && !_stillsExported && _stillProbeKept >= 4 && Output != null)
                {
                    _stillsExported = true;
                    int n = stills.ExportAll(Output.EnsureFolder(), SafeName(), out string sErr);
                    MilErrorLog.Note($"{Name}: stills exported - {n} files"
                                   + (sErr != null ? $", error: {sErr}" : string.Empty));
                }

                // Anomalies are surfaced here rather than from the hook: a handler running on the
                // acquisition thread would put the grab behind whatever it decides to do, and the
                // whole reason the reduction is kept small is to stay out of that budget.
                bool raised = false;
                while (_anomalies.TryDequeue(out AnomalyEvent found))
                {
                    _lastAnomaly = found;
                    _hasLastAnomaly = true;
                    raised = true;
                    MilErrorLog.Note($"{Name}: anomaly {found}");
                    WriteEventWindow(found);
                    AnomalyDetected?.Invoke(this, found);
                }
                if (raised)
                {
                    RaisePropertyChanged(nameof(AnomalyCount));
                    RaisePropertyChanged(nameof(LastAnomalyText));
                    RaisePropertyChanged(nameof(DetectionHint));
                }

                // Rejections are surfaced on the same tick. Only the last one is kept, so a tick
                // that turned away several leaves one window and a count - which is why the count
                // is logged rather than inferred from the files.
                AnomalyDetector detector = _detector;
                if (detector != null && detector.EventsRejectedForSpread > _rejectedSeen)
                {
                    long now = detector.EventsRejectedForSpread;
                    AnomalyEvent? turned = detector.LastRejectedEvent;
                    if (turned.HasValue)
                    {
                        MilErrorLog.Note(
                            $"{Name}: swept, not dimmed - {turned.Value} "
                          + $"(rejected {now} so far)");
                        WriteEventWindow(turned.Value, rejected: true);
                    }
                    _rejectedSeen = now;
                    RaisePropertyChanged(nameof(EventsRejectedForSpread));
                }

                // Detect a disconnected camera (2 consecutive misses to avoid transient blips).
                bool present;
                try { present = MIL.MdigInquire(_digId, MIL.M_CAMERA_PRESENT, MIL.M_NULL) != MIL.M_NO; }
                catch (MILException) { present = false; }

                if (!present)
                {
                    if (!_cameraLost && ++_lostPolls >= 2)
                    {
                        _cameraLost = true;
                        StopRecording();   // safe; leave StopGrab to the user (M_STOP on a dead port is risky)
                        CameraLost?.Invoke(this);
                    }
                }
                else if (_cameraLost || _lostPolls > 0)
                {
                    _cameraLost = false;   // recovered
                    _lostPolls = 0;
                }
            }

            // If ffmpeg died mid-recording, finalize and surface the error to the UI.
            if (_recording != null && _recording.Failed && _recording.IsActive)
            {
                string err = _recording.LastError;
                StopRecording();
                RecordingFailed?.Invoke(this, err ?? "Recording stopped unexpectedly.");
            }

            RaisePropertyChanged(nameof(FrameRate));
            RaisePropertyChanged(nameof(FrameCount));
            RaisePropertyChanged(nameof(FramesMissed));
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(RecordingActive));
            RaisePropertyChanged(nameof(RecordingBannerText));
        }

        private static MIL_INT ProcessFrame(MIL_INT hookType, MIL_ID hookId, IntPtr userDataPtr)
        {
            if (userDataPtr == IntPtr.Zero)
                return 0;

            var data = GCHandle.FromIntPtr(userDataPtr).Target as ChannelHookData;
            if (data == null)
                return 0;

            MIL_ID grabbedBuffer = MIL.M_NULL;
            MIL.MdigGetHookInfo(hookId, MIL.M_MODIFIED_BUFFER + MIL.M_BUFFER_ID, ref grabbedBuffer);

            // The board's own stamp for this frame. Kept because it is the only clock every channel
            // shares — the cameras are free-running, so nothing else lines their frames up, and
            // where the channels are split across processes there is no shared timer at all. Two
            // reads per grab, first and last, so this costs nothing per frame.
            // M_MODIFIED_BUFFER + M_GRAB_TIME_STAMP, the same shape as the buffer id above.
            // M_GRAB_TIME_STAMP_NS is a different constant for a different call and reads back zero
            // here — which is how this was got wrong the first time.
            double timeStampSec = 0;
            MIL.MdigGetHookInfo(hookId, MIL.M_MODIFIED_BUFFER + MIL.M_GRAB_TIME_STAMP, ref timeStampSec);
            if (data.FrameCount == 0) data.FirstTimeStampSec = timeStampSec;
            data.LastTimeStampSec = timeStampSec;

            data.FrameCount++;
            data.Owner?.OnGrabbedFrame(grabbedBuffer, data.DisplayBuffer, data.FrameCount, timeStampSec);
            return 0;
        }

        /// <summary>
        /// Per-frame work on the acquisition thread: copy to the display buffer, and (if
        /// recording) feed the frame to the encoder. Add real inspection here as needed.
        /// </summary>
        private void OnGrabbedFrame(MIL_ID grabbedBuffer, MIL_ID displayBuffer,
                                    long frameNumber, double timeStampSec)
        {
            // Detection first: it reads about 150 kB of the region, which is small beside the
            // 2.3 MB display copy below.
            RunDetection(grabbedBuffer, frameNumber, timeStampSec);

            // ---- Per-frame processing / display update ----
            // The display copy is 2.3 MB (cropped colour) and triggers a UI-thread update, so it
            // runs at DisplayUpdateFps rather than every frame. Recording is unaffected:
            // the sink is fed from the grab buffer, never the display buffer.
            int dispFps = Output?.DisplayUpdateFps ?? 0;
            bool copyToDisplay = true;
            if (dispFps > 0)
            {
                long now = _dispClock.ElapsedMilliseconds;
                long interval = 1000 / dispFps;
                if (now - _lastDispCopyMs < interval)
                    copyToDisplay = false;
                else
                    _lastDispCopyMs = now;
            }
            if (copyToDisplay)
                MIL.MbufCopy(grabbedBuffer, displayBuffer);

            // ---- Lossless stills (probe: rotate every slot rather than wait for an event) ----
            StillRing stills = _stills;
            if (stills != null && frameNumber % StillRing.ReferenceEveryFrames == 0)
            {
                stills.Keep((StillRing.Slot)(_stillProbeKept % 4), grabbedBuffer,
                            frameNumber, timeStampSec, 0.0);
                _stillProbeKept++;
            }

            // ---- Recording feed (the sink guards start/stop vs feed internally) ----
            _recording?.Feed(grabbedBuffer, frameNumber);
        }

        /// <summary>
        /// Reduces this frame to tiles and hands them to the detector. On the acquisition thread.
        ///
        /// The region is read from the grab buffer rather than the display buffer, which is what
        /// makes per-frame judgement possible at all: the display copy runs at DisplayUpdateFps,
        /// so reading it would sample about a quarter of the frames and miss every fault shorter
        /// than 33 ms -- which is most of them.
        /// </summary>
        private void RunDetection(MIL_ID grabbedBuffer, long frameNumber, double timeStampSec)
        {
            if (!DetectionEnabled)
                return;

            AnomalyDetector detector = _detector;
            if (detector == null)
                return;

            // A failed reduction leaves the grid empty, and an empty grid is not a dark one -
            // judging it would read as a total blackout.
            if (!_reducer.Reduce(grabbedBuffer, _analysisRoi, _grid))
                return;

            // After Reduce: it resets the grid, which clears the number.
            _grid.FrameNumber = frameNumber;
            _history.Add(_grid, timeStampSec);

            AnomalyEvent? closed = detector.Observe(_grid, timeStampSec);
            if (closed.HasValue)
                RecordAnomaly(closed.Value);
        }

        /// <summary>
        /// Frames of history written before an event starts. 200 is about 1.6 s at 124.3 fps --
        /// enough to show what the tiles were doing before the fall, which is the difference
        /// between a panel that switched off and something that swept across in front of it.
        /// </summary>
        private const int EventPrerollFrames = 200;

        /// <summary>
        /// Event windows one run will write before it stops. A run that fires constantly is a
        /// misconfiguration, and the diagnosis is in the first few windows either way; without a
        /// cap it would fill the disk while nobody was reading them.
        /// </summary>
        private const int MaxEventWindows = 20;

        /// <summary>
        /// Writes the tile history around a confirmed event. On the stats tick, not the hook: this
        /// touches the disk.
        /// </summary>
        /// <summary>
        /// Windows written for rejected falls. Fewer than for confirmed ones, and counted
        /// separately: on the run this gate was built from, 56 of 60 events were sweeps, and a
        /// shared cap would have filled entirely with them and left no room for the real ones.
        /// </summary>
        private const int MaxRejectedWindows = 5;

        private void WriteEventWindow(AnomalyEvent found, bool rejected = false)
        {
            if (rejected)
            {
                if (_rejectedWindowsWritten >= MaxRejectedWindows) return;
            }
            else if (_eventWindowsWritten >= MaxEventWindows)
            {
                return;
            }

            string kind = rejected ? "rejected" : "event";
            string label = $"{kind}-ch{_index}-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            string path = _history.Write(
                BrightnessLog.DefaultFolder,
                label,
                found.StartFrame - EventPrerollFrames,
                found.EndFrame + DetectionThresholds.DebounceFrames,
                found.StartFrame,
                found.EndFrame);

            if (path == null)
                return;

            if (rejected) _rejectedWindowsWritten++; else _eventWindowsWritten++;
            MilErrorLog.Note($"{Name}: {kind} window -> {path}");
        }

        /// <summary>Queues a confirmed anomaly for the stats tick. On the acquisition thread.</summary>
        private void RecordAnomaly(AnomalyEvent found)
        {
            _anomalies.Enqueue(found);
            Interlocked.Increment(ref _anomalyCount);
        }

        #endregion

        #region View control (fit / zoom / snapshot)

        /// <summary>
        /// Caps the MIL display's update rate at OutputSettings.DisplayUpdateFps (0 = uncapped).
        ///
        /// This buys CPU, not frame rate: with the display switched off entirely the aggregate
        /// acquisition rate did not move, because the ceiling is board/PCIe DMA rather than host
        /// memory bandwidth (research.md section 8). Apply it only once the ROI has taken the
        /// load off — under oversubscription the extra display threads starve a channel.
        /// </summary>
        public void ApplyDisplayUpdateCap()
        {
            if (_dispId == MIL.M_NULL)
                return;
            int fps = Output?.DisplayUpdateFps ?? 0;
            // AllocateBuffers runs during MainWindow construction, so an unsupported control here
            // would previously have opened one modal dialog per channel before the window even
            // exists. MIL error printing is now disabled process-wide; failures land in the log.
            try
            {
                MIL.MdispControl(_dispId, MIL.M_UPDATE_RATE_MAX,
                    fps > 0 ? (double)fps : MIL.M_MAX_REFRESH_RATE);
            }
            catch (MILException e)
            {
                // An uncapped display is a performance regression, not a failure — never take the
                // app down for it.
                MilErrorLog.Write($"{Name}: set display update-rate cap", e);
            }
        }

        // The zoom right after the last fit, to tell "still fitted" from "the operator zoomed in".
        // Interactive zoom is native to MIL and raises no event we could hook, so this is inferred.
        //
        // Only zoom is compared. MIL re-centres the view whenever the display control is resized,
        // so M_REAL_OFFSET_X/Y move with no operator input at all — measured jumping from 0 to -792
        // on a plain resize — and an offset comparison therefore reads every resize as a pan and
        // stops refitting for the rest of the session. Zoom alone is also sufficient: at fit scale
        // the whole image is visible, so there is nothing to pan to, and panning only becomes
        // meaningful once zoomed in, where the zoom already differs.
        private double _fittedZoom;
        private bool _haveFitBaseline;

        /// <summary>Scales the whole image to fit the display control once (aspect preserved).</summary>
        public void FitToWindow()
        {
            if (_dispId == MIL.M_NULL)
                return;
            MIL.MdispControl(_dispId, MIL.M_SCALE_DISPLAY, MIL.M_ONCE);
            CaptureFitBaseline();
        }

        /// <summary>
        /// Re-fits only if the operator has not zoomed or panned since the last fit. The pane calls
        /// this on resize: expanding the Settings expander resizes the view, and an unconditional
        /// fit there discards a zoom the operator set deliberately.
        /// </summary>
        public void FitToWindowIfUntouched()
        {
            if (_dispId == MIL.M_NULL)
                return;
            if (_haveFitBaseline && ViewMovedByOperator())
                return;
            FitToWindow();
        }

        private void CaptureFitBaseline()
        {
            _haveFitBaseline = TryReadZoom(out _fittedZoom);
        }

        private bool ViewMovedByOperator()
        {
            if (!TryReadZoom(out double zoom))
                return false;   // cannot tell — prefer fitting, which is the old behaviour
            return Math.Abs(zoom - _fittedZoom) > 0.001;
        }

        private bool TryReadZoom(out double zoom)
        {
            zoom = 0;
            try
            {
                MIL.MdispInquire(_dispId, MIL.M_REAL_ZOOM_FACTOR_X, ref zoom);
                return true;
            }
            catch (MILException e)
            {
                MilErrorLog.Write($"{Name}: read display zoom factor", e);
                return false;
            }
        }

        /// <summary>Resets zoom to 100% (1:1) and clears any pan offset.</summary>
        public void ZoomActual()
        {
            if (_dispId == MIL.M_NULL)
                return;
            MIL.MdispControl(_dispId, MIL.M_SCALE_DISPLAY, MIL.M_DISABLE);
            MIL.MdispZoom(_dispId, 1.0, 1.0);
            MIL.MdispPan(_dispId, MIL.M_NULL, MIL.M_NULL);
        }

        /// <summary>
        /// Saves the current frame to the configured output folder as
        /// {OutputName}_{yyyyMMdd_HHmmss}.png, applying the resolution preset.
        /// Returns the saved path, or null on failure.
        /// </summary>
        public string SaveSnapshotToOutput()
        {
            OutputSettings settings = Output;
            if (_dispBufId == MIL.M_NULL || settings == null)
                return null;

            // While stopped, the display buffer still holds the last frame of the previous run, so
            // this would save that frame under the current timestamp - and would keep saving it,
            // byte for byte, however the exposure was changed in between. Measured: four snapshots
            // taken across three exposures produced two hashes, both from a run that had already
            // ended. Refusing is the only honest answer.
            if (!_isGrabbing)
            {
                MilErrorLog.Note($"{Name}: snapshot refused - not grabbing, the display buffer holds a stale frame");
                return null;
            }

            try
            {
                string path = System.IO.Path.Combine(settings.EnsureFolder(), $"{SafeName()}_{Timestamp()}.png");

                MIL_INT srcH = MIL.MbufInquire(_dispBufId, MIL.M_SIZE_Y, MIL.M_NULL);
                double scale = settings.ScaleFactorFor(srcH);
                if (scale < 0.999)
                {
                    MIL_ID tmp = AllocScaledBuffer(_dispBufId, scale);
                    MIL.MimResize(_dispBufId, tmp, scale, scale, MIL.M_BILINEAR);
                    MIL.MbufExport(path, MIL.M_PNG, tmp);
                    MIL.MbufFree(tmp);
                }
                else
                {
                    MIL.MbufExport(path, MIL.M_PNG, _dispBufId);
                }
                return path;
            }
            catch (MILException)
            {
                return null;
            }
        }

        #endregion

        #region Recording (delegated to an IVideoSink)

        /// <summary>
        /// Starts recording this camera to {OutputName}_{timestamp}.mp4 in the output folder,
        /// at the configured resolution preset. Requires <see cref="CanRecord"/> (ffmpeg present).
        /// </summary>
        public bool StartRecording()
        {
            if (!CameraPresent || _recording == null)
                return false;
            // The camera's own answer first. The measured rate does not exist yet - RefreshStats
            // fills _frameRate on the stats tick, up to 500 ms from now, and never clears it between
            // runs, so it is either zero or the last run's. And M_SELECTED_FRAME_RATE reports the
            // configured AcquisitionFrameRate (184 on this camera), not what the exposure allows
            // (124.3 at 8000 us) - a header written from it made a 120.0 s recording read as 81.07 s
            // and play 1.48x too fast, with every frame present. Measured 2026-09-10.
            double fps = TryGetResultingFps(out double resulting) && resulting > 1.0
                       ? resulting
                       : _frameRate > 1.0 ? _frameRate : InquireNominalFps();
            var spec = new VideoStreamSpec(
                _dispBufId, fps, Output.EnsureFolder(), SafeName(),
                Output.ScaleFactorFor(MIL.MbufInquire(_dispBufId, MIL.M_SIZE_Y, MIL.M_NULL)),
                new[] { VideoOutputSpec.SingleFile() });
            bool ok = _recording.Start(spec, out _);
            RaisePropertyChanged(nameof(IsRecording));
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(RecordingActive));
            RaisePropertyChanged(nameof(RecordingBannerText));
            return ok;
        }

        /// <summary>Stops recording and finalizes the .mp4 file (finalization runs asynchronously).</summary>
        public void StopRecording()
        {
            if (_recording == null || !_recording.IsActive)
                return;
            _recording.Stop();
            RaisePropertyChanged(nameof(IsRecording));
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(RecordingActive));
            RaisePropertyChanged(nameof(RecordingBannerText));
        }

        /// <summary>Starts recording if idle, stops it if already recording.</summary>
        public bool ToggleRecording()
        {
            if (IsRecording)
            {
                StopRecording();
                return false;
            }
            return StartRecording();
        }

        #endregion

        #region Board Bayer conversion



        /// <summary>
        /// Reads back the board's Bayer conversion state for this channel, as "on"/"off", or "?"
        /// when it cannot be read.
        ///
        /// This exists to settle whether the setting is per-digitizer or board-wide. The API takes
        /// a digitizer, which suggests per-channel, and the app's own RAW recording turns it off for
        /// one channel while the others keep their colour — but nobody has ever read one channel
        /// after writing another, and an architecture that splits the channels across processes
        /// stands or falls on the answer.
        /// </summary>
        public string BayerConversionState()
        {
            if (_digId == MIL.M_NULL) return "-";
            try
            {
                MIL_INT v = 0;
                MIL.MdigInquire(_digId, MIL.M_BAYER_CONVERSION, ref v);
                return v == MIL.M_DISABLE ? "off" : v == MIL.M_ENABLE ? "on" : $"?({v})";
            }
            catch (MILException) { return "?"; }
        }

        /// <summary>Sets Bayer conversion from outside, for the scope diagnostic.</summary>
        public bool SetBayerConversionForDiagnostic(bool enable) => SetBayerConversion(enable);

        /// <summary>Enables/disables the board's hardware Bayer→RGB conversion (disabled = raw band=1).</summary>
        private bool SetBayerConversion(bool enable)
        {
            if (_digId == MIL.M_NULL) return false;
            try { MIL.MdigControl(_digId, MIL.M_BAYER_CONVERSION, enable ? MIL.M_ENABLE : MIL.M_DISABLE); return true; }
            catch (MILException) { return false; }
        }






        private double InquireNominalFps()
        {
            try
            {
                double fps = 0;
                MIL.MdigInquire(_digId, MIL.M_SELECTED_FRAME_RATE, ref fps);
                if (fps > 1.0)
                    return fps;
            }
            catch (MILException e)
            {
                MilErrorLog.Write($"{Name}: read the camera's configured frame rate", e);
            }
            return 30.0;
        }

        #endregion

        #region Output helpers

        /// <summary>Allocates a destination buffer scaled from <paramref name="src"/> by <paramref name="scale"/>.</summary>
        private MIL_ID AllocScaledBuffer(MIL_ID src, double scale, bool evenDims = false)
        {
            MIL_INT band = MIL.MbufInquire(src, MIL.M_SIZE_BAND, MIL.M_NULL);
            MIL_INT type = MIL.MbufInquire(src, MIL.M_TYPE, MIL.M_NULL);
            long srcW = MIL.MbufInquire(src, MIL.M_SIZE_X, MIL.M_NULL);
            long srcH = MIL.MbufInquire(src, MIL.M_SIZE_Y, MIL.M_NULL);
            long dstW = Math.Max(2, (long)(srcW * scale));
            long dstH = Math.Max(2, (long)(srcH * scale));
            if (evenDims) { dstW &= ~1L; dstH &= ~1L; }   // H.264 needs even dimensions
            MIL_ID dst = MIL.M_NULL;
            MIL.MbufAllocColor(_sysId, band, dstW, dstH, type, MIL.M_IMAGE + MIL.M_PROC, ref dst);
            return dst;
        }

        private string SafeName()
        {
            string s = _outputName;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace(' ', '_');
            // Always suffix the channel index so two cameras sharing a name don't collide.
            return $"{s}_ch{_index}";
        }

        private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

        #endregion

        #region Camera feature control (exposure / trigger / DCF / browser)

        /// <summary>Re-reads which features the camera supports and their current values.</summary>
        public void RefreshFeatureState()
        {
            SupportsExposure = FeatureAvailable(F_EXPOSURE_TIME);
            SupportsExposureAuto = FeatureAvailable(F_EXPOSURE_AUTO);
            SupportsTrigger = FeatureAvailable(F_TRIGGER_MODE);

            if (_supportsExposure)
            {
                TryGetFeatureDouble(MIL.M_FEATURE_MIN, F_EXPOSURE_TIME, out _exposureMin);
                TryGetFeatureDouble(MIL.M_FEATURE_MAX, F_EXPOSURE_TIME, out _exposureMax);
            }
            RefreshExposureReadback();
            RaisePropertyChanged(nameof(ExposureRangeHint));

            if (_supportsExposureAuto &&
                TryGetFeatureString(F_EXPOSURE_AUTO, out string autoVal))
            {
                _exposureAuto = !string.Equals(autoVal, "Off", StringComparison.OrdinalIgnoreCase);
                RaisePropertyChanged(nameof(ExposureAuto));
                RaisePropertyChanged(nameof(CanSetExposureManually));
            }

            if (_supportsTrigger)
            {
                if (TryGetFeatureString(F_TRIGGER_MODE, out string mode))
                {
                    _triggerOn = string.Equals(mode, "On", StringComparison.OrdinalIgnoreCase);
                    RaisePropertyChanged(nameof(TriggerOn));
                }

                TriggerSources = GetEnumEntries(F_TRIGGER_SOURCE);
                if (TryGetFeatureString(F_TRIGGER_SOURCE, out string src))
                {
                    _selectedTriggerSource = src;
                    RaisePropertyChanged(nameof(SelectedTriggerSource));
                }
            }
            else
            {
                TriggerSources = new List<string>();
            }

            SupportsAcqRate = FeatureAvailable(F_ACQ_RATE);
            SupportsAcqRateEnable = FeatureAvailable(F_ACQ_RATE_ENABLE);
            if (_supportsAcqRate)
            {
                TryGetFeatureDouble(MIL.M_FEATURE_MAX, F_ACQ_RATE, out _acqRateMax);
                if (_supportsAcqRateEnable && TryGetFeatureBool(F_ACQ_RATE_ENABLE, out bool en))
                {
                    _acqRateEnabled = en;
                    RaisePropertyChanged(nameof(AcqRateEnabled));
                }
                else if (!_supportsAcqRateEnable)
                {
                    _acqRateEnabled = true;   // no enable feature: the rate always applies
                }
                RaisePropertyChanged(nameof(CanSetAcqRate));
                RaisePropertyChanged(nameof(AcqRateHint));
                RefreshAcqRateReadback();
            }

            SupportsWhiteBalance = FeatureAvailable(F_BALANCE_WHITE_AUTO) || FeatureAvailable(F_BALANCE_RATIO);
            if (_supportsWhiteBalance)
            {
                if (TryGetFeatureString(F_BALANCE_WHITE_AUTO, out string wbAuto))
                {
                    _whiteBalanceAuto = string.Equals(wbAuto, "Continuous", StringComparison.OrdinalIgnoreCase);
                    RaisePropertyChanged(nameof(WhiteBalanceAuto));
                    RaisePropertyChanged(nameof(CanSetBalanceManually));
                }
                RefreshWhiteBalanceReadback();
            }

            UpdateCameraInfo();

            RaisePropertyChanged(nameof(SupportsDecimation));
            RaisePropertyChanged(nameof(Decimation));
            RaisePropertyChanged(nameof(DecimationHint));

            RaisePropertyChanged(nameof(AnalysisRoi));
            RaisePropertyChanged(nameof(AnalysisRoiHint));
        }

        private void UpdateCameraInfo()
        {
            if (_digId == MIL.M_NULL)
            {
                CameraInfo = "No camera";
                return;
            }
            MIL_INT sx = MIL.MdigInquire(_digId, MIL.M_SIZE_X, MIL.M_NULL);
            MIL_INT sy = MIL.MdigInquire(_digId, MIL.M_SIZE_Y, MIL.M_NULL);
            string model = "";
            if (!TryGetFeatureString("DeviceModelName", out model))
                model = "";
            string vendor = "";
            TryGetFeatureString("DeviceVendorName", out vendor);
            string who = string.Join(" ", new[] { vendor, model }).Trim();
            CameraInfo = string.IsNullOrEmpty(who) ? $"{sx}×{sy}" : $"{who}  {sx}×{sy}";
        }

        /// <summary>
        /// One-line dump of the acquisition-limiting settings for this camera (exposure, frame-rate
        /// features, link speed, payload) — used to diagnose why the measured rate is below spec.
        /// </summary>
        public string DumpDiagnostics()
        {
            if (_digId == MIL.M_NULL)
                return $"{OutputName}: no camera";

            var sb = new StringBuilder();
            MIL_INT sx = MIL.MdigInquire(_digId, MIL.M_SIZE_X, MIL.M_NULL);
            MIL_INT sy = MIL.MdigInquire(_digId, MIL.M_SIZE_Y, MIL.M_NULL);
            sb.Append($"{OutputName}: {sx}x{sy}");

            // Absent features are skipped silently — GenICamFeatures gates every read on presence.
            if (TryGetFeatureDouble(MIL.M_FEATURE_VALUE, "ExposureTime", out double exp))
                sb.Append($"  Exposure={exp:F0}us(=>{(exp > 0 ? 1e6 / exp : 0):F0}fps max)");
            if (TryGetFeatureString("ExposureAuto", out string expAuto) && !string.IsNullOrEmpty(expAuto))
                sb.Append($"  ExposureAuto={expAuto}");

            foreach (string f in new[] { "AcquisitionFrameRate", "AcquisitionFrameRateMax",
                                         "ResultingFrameRate", "AcquisitionMaxFrameRate",
                                         "DeviceLinkSpeedBps", "DeviceLinkThroughputLimit" })
            {
                if (TryGetFeatureDouble(MIL.M_FEATURE_VALUE, f, out double v))
                    sb.Append($"  {f}={v:F0}");
            }
            foreach (string f in new[] { "AcquisitionFrameRateEnable", "CxpLinkConfiguration",
                                         "CxpLinkConfigurationStatus", "DeviceLinkThroughputLimitMode" })
            {
                if (TryGetFeatureString(f, out string s) && !string.IsNullOrEmpty(s))
                    sb.Append($"  {f}={s}");
            }

            try
            {
                // This camera answers -1 here. A nonsense number in a diagnostic line is worse than
                // no number, so only print it when it is one.
                MIL_INT payload = MIL.MdigInquire(_digId, MIL.M_GC_PAYLOAD_SIZE, MIL.M_NULL);
                if ((long)payload > 0)
                    sb.Append($"  payload={(long)payload}B");
            }
            catch (MILException e) { MilErrorLog.Write($"{Name}: read GenICam payload size", e); }

            return sb.ToString();
        }

        private void RefreshExposureReadback()
        {
            if (_supportsExposure && TryGetFeatureDouble(MIL.M_FEATURE_VALUE, F_EXPOSURE_TIME, out double v))
            {
                ExposureInput = v.ToString("0.##", CultureInfo.InvariantCulture);
                _exposureUs = v;
                RaisePropertyChanged(nameof(ExposureUs));
            }
        }

        /// <summary>Parses the exposure input box and applies it to the camera.</summary>
        public bool ApplyExposure()
        {
            if (!_supportsExposure)
                return false;
            if (!double.TryParse(_exposureInput, NumberStyles.Float, CultureInfo.InvariantCulture, out double us))
                return false;

            return WriteExposureUs(us);
        }

        private bool SetExposureAuto(bool on)
        {
            return TrySetFeatureString(F_EXPOSURE_AUTO, on ? "Continuous" : "Off");
        }

        private void RefreshAcqRateReadback()
        {
            if (_supportsAcqRate && TryGetFeatureDouble(MIL.M_FEATURE_VALUE, F_ACQ_RATE, out double v))
                AcqRateInput = v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Applies the acquisition-rate cap (enables it first if the camera has the toggle).</summary>
        public bool ApplyAcqRate()
        {
            if (!_supportsAcqRate)
                return false;
            if (!double.TryParse(_acqRateInput, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps))
                return false;

            if (_supportsAcqRateEnable && !_acqRateEnabled)
            {
                if (SetFeatureBool(F_ACQ_RATE_ENABLE, true))
                {
                    _acqRateEnabled = true;
                    RaisePropertyChanged(nameof(AcqRateEnabled));
                    RaisePropertyChanged(nameof(CanSetAcqRate));
                }
            }

            bool ok = SetFeatureDouble(F_ACQ_RATE, fps);
            // Max can change with the enable/exposure state.
            TryGetFeatureDouble(MIL.M_FEATURE_MAX, F_ACQ_RATE, out _acqRateMax);
            RaisePropertyChanged(nameof(AcqRateHint));
            RefreshAcqRateReadback();
            return ok;
        }

        private void RefreshWhiteBalanceReadback()
        {
            RedRatioInput = ReadBalanceRatio("Red");
            BlueRatioInput = ReadBalanceRatio("Blue");
        }

        private string ReadBalanceRatio(string band)
        {
            if (!FeatureAvailable(F_BALANCE_RATIO))
                return "";
            if (!TrySetFeatureString(F_BALANCE_RATIO_SELECTOR, band))
                return "";
            if (TryGetFeatureDouble(MIL.M_FEATURE_VALUE, F_BALANCE_RATIO, out double v))
                return v.ToString("0.###", CultureInfo.InvariantCulture);
            return "";
        }

        private bool SetWhiteBalanceAuto(bool on)
        {
            return TrySetFeatureString(F_BALANCE_WHITE_AUTO, on ? "Continuous" : "Off");
        }

        /// <summary>Runs a one-shot automatic white balance (BalanceWhiteAuto = Once).</summary>
        public bool WhiteBalanceOnce()
        {
            bool ok = TrySetFeatureString(F_BALANCE_WHITE_AUTO, "Once");
            RefreshWhiteBalanceReadback();
            return ok;
        }

        /// <summary>Applies the manually entered Red/Blue balance ratios (Green is the 1.0 reference).</summary>
        public bool ApplyBalanceRatios()
        {
            if (!_supportsWhiteBalance || !FeatureAvailable(F_BALANCE_RATIO))
                return false;
            bool ok = true;
            ok &= SetBalanceRatio("Red", _redRatioInput);
            ok &= SetBalanceRatio("Blue", _blueRatioInput);
            RefreshWhiteBalanceReadback();
            return ok;
        }

        private bool SetBalanceRatio(string band, string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return false;
            if (!TrySetFeatureString(F_BALANCE_RATIO_SELECTOR, band))
                return false;
            return SetFeatureDouble(F_BALANCE_RATIO, v);
        }

        /// <summary>Enables/disables the frame-start trigger.</summary>
        public bool SetTriggerMode(bool on)
        {
            // Select the frame-start trigger first if the camera exposes a selector.
            TrySetFeatureString(F_TRIGGER_SELECTOR, "FrameStart");
            return TrySetFeatureString(F_TRIGGER_MODE, on ? "On" : "Off");
        }

        /// <summary>Fires one software trigger (executes the TriggerSoftware command feature).</summary>
        public bool FireSoftwareTrigger() => _features.ExecuteCommand(F_TRIGGER_SOFTWARE);

        /// <summary>Opens the interactive GenICam feature browser for full camera configuration.</summary>
        public void OpenFeatureBrowser()
        {
            if (_digId == MIL.M_NULL)
                return;
            try
            {
                MIL.MdigControl(_digId, MIL.M_GC_FEATURE_BROWSER, MIL.M_OPEN + MIL.M_ASYNCHRONOUS);
            }
            catch (MILException e)
            {
                MilErrorLog.Write($"{Name}: open GenICam feature browser", e);
            }
        }

        /// <summary>
        /// Reallocates the digitizer/buffers using a different DCF (camera configuration file).
        /// Pass "M_DEFAULT" to use the driver default. Resumes grabbing if it was active.
        /// </summary>
        public bool ReloadWithDcf(string dcfName)
        {
            bool wasGrabbing = _isGrabbing;
            FreeCamera();
            _cameraAvailable = true; // an explicit DCF load is a deliberate attempt to use a camera
            DcfName = string.IsNullOrWhiteSpace(dcfName) ? "M_DEFAULT" : dcfName;
            AllocateCamera();
            if (wasGrabbing)
                TryStartGrab();   // a bad DCF must not crash the app from the click handler
            RaisePropertyChanged(nameof(DisplayId));
            return CameraPresent;
        }

        /// <summary>
        /// Writes an integer feature only when it is not already at the target value.
        ///
        /// Some nodes are read-only in states where their current value is the only legal one —
        /// this camera locks OffsetX while Width is at maximum, so writing the 0 it already holds
        /// is rejected outright. Treating "already correct" as success keeps a harmless no-op from
        /// looking like a hardware refusal.
        /// </summary>
        private bool SetIntIfDifferent(string feature, long value)
        {
            if (_features.TryGetInt(MIL.M_FEATURE_VALUE, feature, out long current) && current == value)
                return true;
            return _features.SetInt(feature, value);
        }

        /// <summary>
        /// Records how the run that just ended actually went: frames, rate, frames missed, and the
        /// payload that produced them. Missed frames are re-inquired rather than taken from the
        /// last stats tick, so the number is the run.s final count and not one up to half a second
        /// stale.
        /// </summary>
        private void LogGrabSummary()
        {
            long missed = _framesMissed;
            try
            {
                MIL_INT m = 0;
                MIL.MdigInquire(_digId, MIL.M_PROCESS_FRAME_MISSED, ref m);
                missed = Math.Max(0, (long)m - _missedAtGrabStart);   // this run only
            }
            catch (MILException) { /* keep the last polled value */ }

            MilErrorLog.Note($"{Name}: grab stopped - {FrameCount} frames, {_frameRate:F1} fps, "
                           + $"{missed} missed, {BytesPerFrame / 1048576.0:F2} MiB/frame, decim {_decimation}");

            // What detection cost and what it found, on the same line as the losses it must not
            // have caused. The worst reduction matters more than the average: the frame period at
            // 124 fps is 8045 us, and one reduction over that is a missed frame.
            if (DetectionEnabled)
                MilErrorLog.Note($"{Name}: detection - {AnomalyCount} anomalies, "
                               + $"{EventsRejectedForSpread} swept and turned away, "
                               + $"reduce mean {_reducer.MeanReduceUs:F0} us / max {_reducer.MaxReduceUs:F0} us "
                               + $"of the {(_frameRate > 0 ? 1e6 / _frameRate : 0):F0} us frame period, "
                               + $"{_reducer.Accepted}/{_reducer.Reductions} grids accepted"
                               + (ReducerFailures > 0 ? $", {ReducerFailures} consecutive failures" : string.Empty));

            // What recording cost and what it lost. The extraction here is the whole frame, and
            // it runs in the acquisition hook - so this is also the measurement for whether a
            // preroll ring of frames is affordable at all, since such a ring would have to pay the
            // same extraction on every frame whether or not an event ever follows.
            VideoSinkStats rec = _recording?.Stats ?? default;
            if (rec.FramesFed > 0 || rec.FramesSkipped > 0)
            {
                MilErrorLog.Note($"{Name}: recording ({_recording.Name}) - {rec.FramesFed} fed, "
                               + $"{rec.FramesSkipped} skipped (encoder full), {rec.FramesDropped} dropped, "
                               + $"{rec.WrittenFps:F1} fps written vs {rec.DeclaredFps:F2} fps declared "
                               + $"over {rec.ElapsedSeconds:F1} s, "
                               + $"extract mean {rec.MeanFeedUs:F0} us / max {rec.MaxFeedUs:F0} us "
                               + $"of the {(_frameRate > 0 ? 1e6 / _frameRate : 0):F0} us frame period");
            }

            // The board's stamps for this run, in milliseconds. Whether these share one clock across
            // channels is the whole of cross-channel correlation: the cameras free-run, so nothing
            // else lines their frames up, and split across processes there is no shared timer to
            // fall back on. Comparable numbers here mean correlation needs no IPC at all.
            ChannelHookData d = _hookData;
            if (d != null)
                MilErrorLog.Note($"{Name}: board timestamps - first {d.FirstTimeStampSec:F6} s, "
                               + $"last {d.LastTimeStampSec:F6} s, "
                               + $"span {(d.LastTimeStampSec - d.FirstTimeStampSec) * 1000:F1} ms");
        }

        /// <summary>
        /// Logs the camera's own access mode for the geometry nodes, once per allocation.
        ///
        /// This app concluded that cropping is unsupported by writing `Width` / `OffsetX` and
        /// reading the value back unchanged. That evidence cannot separate two very different
        /// causes: a node this camera implements as read-only, and a node temporarily locked
        /// because the app was killed last run (CLAUDE.md — the symptom is an identical silent
        /// refusal). `M_FEATURE_ACCESS_MODE` is the camera answering directly, so the log says
        /// which one it is instead of leaving the next person to re-derive it.
        /// </summary>
        private void LogGeometryAccess()
        {
            if (_digId == MIL.M_NULL)
                return;
            var sb = new StringBuilder();
            foreach (string f in new[] { F_WIDTH, F_HEIGHT, F_OFFSET_X, F_OFFSET_Y, F_DECIM_H, F_DECIM_V })
            {
                if (!_features.Available(f)) { sb.Append($" {f}=absent"); continue; }
                _features.TryGetInt(MIL.M_FEATURE_VALUE, f, out long v);
                sb.Append($" {f}={v}/{_features.AccessMode(f)}");
            }
            MilErrorLog.Note($"{Name}: geometry nodes (value/access):{sb}");
        }

        /// <summary>
        /// Writes the decimation factor and CONFIRMS IT BY READING IT BACK.
        ///
        /// The read-back is not belt-and-braces. This camera accepts writes to the geometry
        /// features and silently ignores them: MdigControlFeature does not throw, nothing prints
        /// under M_PRINT_DISABLE, and the wrapper therefore returns true while the value never
        /// changes. That cost several rounds of debugging on the crop path before anyone read the
        /// value back. Decimation does take effect here — but the only way to know is to look.
        /// Called from AllocateCamera BEFORE AllocateBuffers, so M_SIZE_X/M_SIZE_Y reflect it.
        /// </summary>
        private void WriteDecimationToCamera()
        {
            if (_digId == MIL.M_NULL || !SupportsDecimation)
            {
                _decimation = 1;
                return;
            }

            int wanted = ChannelRoi.ClampDecimation(Output?.GetDecimation(_index) ?? 1);

            SetIntIfDifferent(F_DECIM_H, wanted);
            SetIntIfDifferent(F_DECIM_V, wanted);

            // Read back, and believe only this.
            long actualH = 1, actualV = 1;
            _features.TryGetInt(MIL.M_FEATURE_VALUE, F_DECIM_H, out actualH);
            _features.TryGetInt(MIL.M_FEATURE_VALUE, F_DECIM_V, out actualV);
            _decimation = (actualH == wanted && actualV == wanted)
                ? wanted
                : ChannelRoi.ClampDecimation((int)actualH);
        }

        /// <summary>Size of the frames actually arriving, which is what the ROI is clamped to.</summary>
        private bool TryGetFrameSize(out int width, out int height)
        {
            width = 0; height = 0;
            if (_dispBufId == MIL.M_NULL) return false;
            try
            {
                width = (int)MIL.MbufInquire(_dispBufId, MIL.M_SIZE_X, MIL.M_NULL);
                height = (int)MIL.MbufInquire(_dispBufId, MIL.M_SIZE_Y, MIL.M_NULL);
                return width > 0 && height > 0;
            }
            catch (MILException e)
            {
                MilErrorLog.Write($"{Name}: read frame size for the analysis ROI", e);
                return false;
            }
        }

        /// <summary>
        /// Current view geometry, for drawing over the live image. False when it cannot be read.
        /// </summary>
        public bool TryGetViewGeometry(out int frameWidth, out int frameHeight,
                                      out double zoom, out double offsetX, out double offsetY)
        {
            zoom = 0; offsetX = 0; offsetY = 0;
            if (!TryGetFrameSize(out frameWidth, out frameHeight))
                return false;
            try
            {
                MIL.MdispInquire(_dispId, MIL.M_REAL_ZOOM_FACTOR_X, ref zoom);
                MIL.MdispInquire(_dispId, MIL.M_REAL_OFFSET_X, ref offsetX);
                MIL.MdispInquire(_dispId, MIL.M_REAL_OFFSET_Y, ref offsetY);
                return zoom > 0;
            }
            catch (MILException e)
            {
                MilErrorLog.Write($"{Name}: read view geometry for the ROI overlay", e);
                return false;
            }
        }

        /// <summary>
        /// Applies the four input boxes as the analysis region. No reallocation: this only changes
        /// which pixels we measure. Returns false only if a box is not an integer.
        /// </summary>
        public bool ApplyAnalysisRoiFromInputs()
        {
            if (!int.TryParse(_roiInX, out int x) || !int.TryParse(_roiInY, out int y) ||
                !int.TryParse(_roiInW, out int w) || !int.TryParse(_roiInH, out int h))
                return false;
            SetAnalysisRoi(new ChannelRoi(x, y, w, h));
            return true;
        }

        /// <summary>Back to measuring the whole frame.</summary>
        public void ClearAnalysisRoi() => SetAnalysisRoi(ChannelRoi.FullFrame);

        /// <summary>
        /// Sets the analysis region from a mouse drag, in image pixels. Values are snapped to the
        /// even grid and clamped inside the frame, exactly as typed input is — a drag that ran off
        /// the edge of the image is normal, not an error.
        /// </summary>
        public void SetAnalysisRoiFromDrag(int offsetX, int offsetY, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;
            SetAnalysisRoi(new ChannelRoi(Math.Max(0, offsetX), Math.Max(0, offsetY), width, height));
        }

        private void SetAnalysisRoi(ChannelRoi requested)
        {
            ChannelRoi snapped = requested;
            if (!requested.IsFullFrame && TryGetFrameSize(out int fw, out int fh))
                snapped = requested.Snap(fw, fh);

            _analysisRoi = snapped;
            Output?.SetRoi(_index, snapped);
            SyncRoiInputs();
            RaisePropertyChanged(nameof(AnalysisRoi));
            RaisePropertyChanged(nameof(AnalysisRoiHint));
        }

        /// <summary>Writes the applied (snapped) values back, so the operator sees what was taken.</summary>
        private void SyncRoiInputs()
        {
            RoiInX = _analysisRoi.OffsetX.ToString(CultureInfo.InvariantCulture);
            RoiInY = _analysisRoi.OffsetY.ToString(CultureInfo.InvariantCulture);
            RoiInW = (_analysisRoi.IsFullFrame ? 0 : _analysisRoi.Width).ToString(CultureInfo.InvariantCulture);
            RoiInH = (_analysisRoi.IsFullFrame ? 0 : _analysisRoi.Height).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Applies a decimation factor: persists it and reallocates the digitizer and buffers.
        /// Resumes grabbing if it was active. Mirrors <see cref="ReloadWithDcf"/>, which solves the
        /// same reallocation problem for the DCF. Returns false if the camera did not take it.
        /// </summary>
        public bool ApplyDecimation(int factor)
        {
            if (!CameraPresent || !SupportsDecimation)
                return false;

            int wanted = ChannelRoi.ClampDecimation(factor);
            bool wasGrabbing = _isGrabbing;
            if (wasGrabbing)
                StopGrab();

            OutputSettings settings = Output;
            settings?.SetDecimation(_index, wanted);

            // Move the analysis rectangle with the frame. It is stored in coordinates of the
            // decimated frame, so halving the factor doubles the frame under a rectangle that does
            // not move — and one drawn at decimation 1 lands outside a decimation-2 frame
            // altogether. This has to happen before AllocateCamera, which reloads the value.
            if (settings != null && _decimation != wanted)
                settings.SetRoi(_index, settings.GetRoi(_index).Rescale(_decimation, wanted));

            FreeCamera();
            _cameraAvailable = true;
            AllocateCamera();

            if (wasGrabbing)
                TryStartGrab();

            RaisePropertyChanged(nameof(DisplayId));
            RaisePropertyChanged(nameof(Decimation));
            RaisePropertyChanged(nameof(DecimationHint));
            return CameraPresent && _decimation == wanted;
        }

        #endregion

        #region Feature helpers (delegate to GenICamFeatures)

        private bool FeatureAvailable(string name) => _features.Available(name);
        private bool TrySetFeatureString(string name, string value) => _features.SetString(name, value);
        private bool SetFeatureDouble(string name, double value) => _features.SetDouble(name, value);
        private bool SetFeatureBool(string name, bool value) => _features.SetBool(name, value);
        private bool TryGetFeatureDouble(long inquireType, string name, out double value) => _features.TryGetDouble(inquireType, name, out value);
        private bool TryGetFeatureString(string name, out string value) => _features.TryGetString(name, out value);
        private bool TryGetFeatureBool(string name, out bool value) => _features.TryGetBool(name, out value);
        private List<string> GetEnumEntries(string feature) => _features.EnumEntries(feature);

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
