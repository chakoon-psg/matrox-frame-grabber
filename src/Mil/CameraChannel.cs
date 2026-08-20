using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

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
        private const string F_ACQ_RATE_ENABLE = "AcquisitionFrameRateEnable";
        private const string F_BALANCE_WHITE_AUTO = "BalanceWhiteAuto";
        private const string F_BALANCE_RATIO_SELECTOR = "BalanceRatioSelector";
        private const string F_BALANCE_RATIO = "BalanceRatio";
        private const string F_WIDTH = "Width";
        private const string F_HEIGHT = "Height";
        private const string F_WIDTH_MAX = "WidthMax";
        private const string F_HEIGHT_MAX = "HeightMax";
        private const string F_OFFSET_X = "OffsetX";
        private const string F_OFFSET_Y = "OffsetY";

        #endregion

        #region Hook data

        private class ChannelHookData
        {
            public CameraChannel Owner;
            public MIL_ID DisplayBuffer;
            public long FrameCount;
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

        // Output naming + recording (delegated to RecordingSession).
        private string _outputName;
        private RecordingSession _recording;

        // Continuous, segmented lossless RAW-Bayer recording (band=1 scratch segments → ffmpeg → MP4).
        private const int RAW_GRAB_BUFFERS = 24;     // deep ring; band=1 frames are ~3x smaller
        private const int RAW_DISPLAY_EVERY = 6;     // grayscale preview ~30fps at 184fps
        private volatile RawSegmentSession _rawSegments;   // active recording (hook writes to it), or null
        private RawSegmentSession _rawFinishing;           // stopped, still transcoding in background
        private bool _rawRecording;
        private bool _rawResumeGrab;
        private bool _rawConverting;
        private int _rawW, _rawH, _rawDisplayCounter;
        private long _rawMissed;
        private DateTime _rawStartTime;

        // GenICam SFNC feature access (its Digitizer is updated on each (re)allocation).
        private readonly GenICamFeatures _features = new GenICamFeatures();

        // Acquisition ROI (crop on the camera to reduce host DMA traffic — research.md section 8).
        private ChannelRoi _roi = ChannelRoi.FullFrame;
        private long _sensorMaxW, _sensorMaxH;
        private int _roiIncX = 2, _roiIncY = 2, _roiIncW = 2, _roiIncH = 2;
        private string _roiInputX = "0", _roiInputY = "0", _roiInputW = "0", _roiInputH = "0";

        // Camera-disconnect detection (polled in RefreshStats; 2 strikes to avoid false positives).
        private bool _cameraLost;
        private int _lostPolls;

        #endregion

        public CameraChannel(int index)
        {
            _index = index;
            _outputName = $"Camera {_index}";

            // StartGrab throws on MIL failure (StartRawRecording relies on that to restore the
            // board), so the command binds to the non-throwing wrapper instead — an unhandled
            // MILException on the UI thread would take the app down.
            StartCommand = new RelayCommand(() => TryStartGrab(), () => CameraPresent);
            StopCommand = new RelayCommand(StopGrab, () => CameraPresent);
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

        /// <summary>True while EITHER a color or a RAW recording is active (drives the pane banner).</summary>
        public bool RecordingActive => _rawRecording || IsRecording;

        /// <summary>Prominent banner text shown over the live view while recording (mode + timer + missed/drops).</summary>
        public string RecordingBannerText
        {
            get
            {
                if (_rawRecording && _rawSegments != null)
                {
                    var t = DateTime.Now - _rawStartTime;
                    string missed = _rawMissed > 0 ? $"     ⚠ missed {_rawMissed}" : "";
                    return $"◆ RAW 무손실 녹화 중 — 프리뷰는 흑백입니다     seg {_rawSegments.SegmentIndex} · 총 {(int)t.TotalMinutes:00}:{t.Seconds:00}{missed}";
                }
                if (IsRecording)
                    return $"● 라이브 녹화 중 (H.264, 고fps 시 프레임 드랍){_recording?.StatusSuffix()}";
                return "";
            }
        }
        public bool CameraPresent => _digId != MIL.M_NULL;
        public bool IsGrabbing => _isGrabbing;
        public long FrameCount => _hookData?.FrameCount ?? 0;
        public double FrameRate => _frameRate;

        /// <summary>This channel's brightness readings, appended on each stats tick.</summary>
        public BrightnessHistory Brightness => _brightness.History;

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
                if (_rawRecording)
                    return $"Grabbing  {FrameRate:F1} fps  ({FrameCount} frames){RawStatusSuffix()}";
                if (_rawConverting)
                    return "Converting raw → MP4…";
                string rec = _recording?.StatusSuffix() ?? "";
                if (_isGrabbing)
                    return $"Grabbing  {FrameRate:F1} fps  ({FrameCount} frames){rec}";
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

        // ----- Acquisition ROI (crop) -----

        /// <summary>The ROI currently applied on the camera (already snapped to the hardware grid).</summary>
        public ChannelRoi Roi => _roi;

        /// <summary>True when the camera exposes a settable acquisition ROI.</summary>
        public bool SupportsRoi => FeatureAvailable(F_WIDTH) && FeatureAvailable(F_OFFSET_X);

        /// <summary>Sensor bounds and step, for the ROI input hint.</summary>
        public string RoiHint => _sensorMaxW > 0
            ? $"max {_sensorMaxW}x{_sensorMaxH} · step {_roiIncW}"
            : "no camera";

        public string RoiInputX { get => _roiInputX; set { _roiInputX = value; RaisePropertyChanged(nameof(RoiInputX)); } }
        public string RoiInputY { get => _roiInputY; set { _roiInputY = value; RaisePropertyChanged(nameof(RoiInputY)); } }
        public string RoiInputW { get => _roiInputW; set { _roiInputW = value; RaisePropertyChanged(nameof(RoiInputW)); } }
        public string RoiInputH { get => _roiInputH; set { _roiInputH = value; RaisePropertyChanged(nameof(RoiInputH)); } }

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
            _recording = new RecordingSession(_sysId);

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
                // raises a "camera not found" error, so suppress MIL error prints for the
                // probe and treat any failure as simply "no camera on this port".
                MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
                try
                {
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
                }
                finally
                {
                    // Always restore error printing, even if an unexpected exception escaped above.
                    MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE);
                }

                if (_digId != MIL.M_NULL)
                {
                    // The feature wrapper needs the digitizer id before anything below asks it a
                    // question: Available() returns false on a null id, so a ROI write placed
                    // above this line silently does nothing. The assignment after AllocateBuffers
                    // stays, because the no-camera path must still clear a stale id.
                    _features.Digitizer = _digId;

                    // Force hardware Bayer→RGB conversion ON (color). M_BAYER_CONVERSION is a
                    // PERSISTENT board setting: once disabled (e.g. to grab raw band-1 Bayer) it
                    // stays off across grabs and app restarts, and the color pipeline then misreads
                    // the mono/raw data as a tiled/garbled image. Re-assert it here, before inquiring
                    // M_SIZE_BAND, so buffers are always 3-band color. (Softly ignored on mono
                    // cameras that have no Bayer filter.)
                    MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
                    try { MIL.MdigControl(_digId, MIL.M_BAYER_CONVERSION, MIL.M_ENABLE); }
                    catch (MILException) { }
                    finally { MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE); }

                    // Recording is done by piping frames to ffmpeg. Enable Rec only if ffmpeg is found.
                    CanRecord = FfmpegRecorder.ResolveFfmpegPath(Output?.FfmpegPath) != null;
                    RaisePropertyChanged(nameof(CanRecord));

                    // ROI before AllocateBuffers: the buffer sizes come from M_SIZE_X/M_SIZE_Y,
                    // which only reflect the crop once it is written. Cropping here is what makes
                    // 3 colour channels at 184 fps fit the host DMA ceiling at all.
                    _roi = Output?.GetRoi(_index) ?? ChannelRoi.FullFrame;
                    WriteRoiToCamera();
                }
            }

            AllocateBuffers(REQUESTED_GRAB_BUFFERS);

            _features.Digitizer = _digId;   // may be M_NULL (no camera) — features then fail softly
            _cameraLost = false; _lostPolls = 0;
            RefreshFeatureState();

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
            ApplyDisplayUpdateCap();

            if (CameraPresent)
            {
                // Allocate as many grab buffers as the non-paged pool allows. With
                // M_THROW_EXCEPTION enabled a shortfall would otherwise abort startup, so each
                // allocation is guarded and we simply stop once the pool is exhausted.
                MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
                try
                {
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
                finally
                {
                    MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE);
                }
            }
        }

        /// <summary>
        /// Frees the grab ring + display buffer, keeping the digitizer and display alive.
        /// <paramref name="resetBrightness"/> is false only for the RAW-recording transition
        /// (start and restore), where the whole point of choosing Rec.601 was a graph that stays
        /// continuous across the color/Bayer switch. Every other caller (camera free, DCF reload)
        /// keeps the default, since the display genuinely goes blank there.
        /// </summary>
        private void FreeBuffers(bool resetBrightness = true)
        {
            foreach (MIL_ID buf in _grabBuffers)
            {
                if (buf != MIL.M_NULL)
                    MIL.MbufFree(buf);
            }
            _grabBuffers.Clear();

            if (_dispId != MIL.M_NULL)
                MIL.MdispSelect(_dispId, MIL.M_NULL);

            if (resetBrightness)
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
            // Stop any raw capture first so the board's Bayer conversion is restored to color
            // (it's a persistent setting) and the current segment is flushed before we free MIL.
            if (_rawRecording)
                StopRawRecording();
            // Give in-flight segment conversions a bounded chance to finish on shutdown (unconverted
            // .raw segments are otherwise left in the scratch folder as a lossless fallback).
            if (_rawFinishing != null) { try { _rawFinishing.WaitConversions(15000); } catch { } }

            StopGrab();   // also stops recording (kicks off async finalize)

            // Wait for any in-flight recording finalize before freeing MIL buffers/system.
            _recording?.WaitFinalize(15000);

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

            _isGrabbing = true;
            RaisePropertyChanged(nameof(IsGrabbing));
            RaisePropertyChanged(nameof(StatusText));
        }

        /// <summary>
        /// Starts acquisition without throwing: a MIL failure is reported through
        /// <see cref="GrabFailed"/> and returned as false. Use this from UI/bulk callers —
        /// <see cref="StartGrab"/> itself still throws, because <see cref="StartRawRecording"/>
        /// depends on catching that to put the board back into colour mode.
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

            // A stopped channel is no longer measuring anything; keeping the old readings would
            // have the strip assert a value nobody is watching anymore.
            _brightness.Reset();

            if (_hookHandle.IsAllocated)
                _hookHandle.Free();
            _hookDelegate = null;

            _isGrabbing = false;
            RaisePropertyChanged(nameof(IsGrabbing));
            RaisePropertyChanged(nameof(StatusText));
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
                if (BrightnessEnabled)
                    _brightness.Sample(_dispBufId);

                // While RAW-recording, track frames the board missed (queue back-pressure on a slow
                // sink), so a "lossless" capture that actually lost frames is visible in the status.
                if (_rawRecording)
                {
                    MIL_INT missed = 0;
                    try { MIL.MdigInquire(_digId, MIL.M_PROCESS_FRAME_MISSED, ref missed); }
                    catch (MILException) { }
                    _rawMissed = missed;
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

            // A RAW segment writer/convert died (disk full, NAS error, ffmpeg fail) mid-recording —
            // stop now; the finish-polling below then surfaces the error to the UI.
            if (_rawRecording && _rawSegments != null && _rawSegments.Failed)
                StopRawRecording();

            // Poll a stopped session's background segment conversions; surface the result once done.
            if (_rawFinishing != null && _rawFinishing.WaitConversions(0))
            {
                RawSegmentSession s = _rawFinishing;
                _rawFinishing = null;
                _rawConverting = false;
                bool ok = !s.Failed;
                string msg = ok
                    ? $"{s.SegmentsCompleted}개 세그먼트 저장 완료 → {Output?.OutputFolder}"
                    : ("일부 세그먼트 변환/저장 오류: " + (s.LastError ?? "unknown"));
                s.Dispose();
                RaisePropertyChanged(nameof(StatusText));
                RawRecordingFinished?.Invoke(this, ok, msg);
            }

            RaisePropertyChanged(nameof(FrameRate));
            RaisePropertyChanged(nameof(FrameCount));
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

            data.FrameCount++;
            data.Owner?.OnGrabbedFrame(grabbedBuffer, data.DisplayBuffer);
            return 0;
        }

        /// <summary>
        /// Per-frame work on the acquisition thread: copy to the display buffer, and (if
        /// recording) feed the frame to the encoder. Add real inspection here as needed.
        /// </summary>
        private void OnGrabbedFrame(MIL_ID grabbedBuffer, MIL_ID displayBuffer)
        {
            // ---- Lossless RAW capture: write EVERY frame to the current segment; grayscale preview ----
            RawSegmentSession seg = _rawSegments;
            if (seg != null)
            {
                byte[] buf = seg.Rent();             // rolls to a new segment if the current one is full
                // MbufGet2d copies the logical W×H region PACKED. MbufGet would copy the row-padded
                // buffer (pitch 2112 > width 2064), shearing the raw when read back as tight 2064 rows.
                MIL.MbufGet2d(grabbedBuffer, 0, 0, _rawW, _rawH, buf);
                seg.Feed(buf);
                if (++_rawDisplayCounter >= RAW_DISPLAY_EVERY)
                {
                    _rawDisplayCounter = 0;
                    MIL.MbufCopy(grabbedBuffer, displayBuffer);   // band1 → band1 (grayscale)
                }
                return;
            }

            // ---- Per-frame processing / display update ----
            MIL.MbufCopy(grabbedBuffer, displayBuffer);

            // ---- Recording feed (RecordingSession guards start/stop vs feed internally) ----
            _recording?.Feed(grabbedBuffer);
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
            try
            {
                MIL.MdispControl(_dispId, MIL.M_UPDATE_RATE_MAX,
                    fps > 0 ? (double)fps : MIL.M_MAX_REFRESH_RATE);
            }
            catch (MILException)
            {
                // An older board/driver may not support the control. An uncapped display is a
                // performance regression, not a failure — never take the app down for it.
            }
        }

        /// <summary>Scales the whole image to fit the display control once (aspect preserved).</summary>
        public void FitToWindow()
        {
            if (_dispId == MIL.M_NULL)
                return;
            MIL.MdispControl(_dispId, MIL.M_SCALE_DISPLAY, MIL.M_ONCE);
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

        #region Recording (delegated to RecordingSession)

        /// <summary>
        /// Starts recording this camera to {OutputName}_{timestamp}.mp4 in the output folder,
        /// at the configured resolution preset. Requires <see cref="CanRecord"/> (ffmpeg present).
        /// </summary>
        public bool StartRecording()
        {
            if (!CameraPresent || _recording == null || _rawRecording)
                return false;
            double fps = _frameRate > 1.0 ? _frameRate : InquireNominalFps();
            bool ok = _recording.Start(_dispBufId, Output, SafeName(), fps, out _);
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

        #region Lossless RAW-Bayer recording

        /// <summary>True while a lossless raw-Bayer capture is in progress.</summary>
        public bool IsRawRecording => _rawRecording;

        /// <summary>Raised (on the UI thread, via RefreshStats) when a raw recording's MP4 conversion
        /// finishes; ok=false carries an error message.</summary>
        public event Action<CameraChannel, bool, string> RawRecordingFinished;

        /// <summary>Enables/disables the board's hardware Bayer→RGB conversion (disabled = raw band=1).</summary>
        private bool SetBayerConversion(bool enable)
        {
            if (_digId == MIL.M_NULL) return false;
            MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
            try { MIL.MdigControl(_digId, MIL.M_BAYER_CONVERSION, enable ? MIL.M_ENABLE : MIL.M_DISABLE); return true; }
            catch (MILException) { return false; }
            finally { MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE); }
        }

        /// <summary>
        /// Starts a continuous lossless capture: disables hardware Bayer conversion (raw band=1), grabs
        /// every frame with a deep DMA ring, and writes rolling N-second RAW segments to the local
        /// scratch folder — each of which a background thread transcodes to a color MP4 in the output
        /// folder (then deletes the .raw). Shows a throttled grayscale preview. Runs until
        /// <see cref="StopRawRecording"/>.
        /// </summary>
        public bool StartRawRecording(out string error)
        {
            error = null;
            if (_digId == MIL.M_NULL) { error = "No camera."; return false; }
            if (_rawRecording) return true;
            if (IsRecording) { error = "Stop the color recording first."; return false; }
            if (!CanRecord) { error = "ffmpeg was not found (needed to convert the recording)."; return false; }

            string outDir, scratch;
            try { outDir = Output?.EnsureFolder(); scratch = Output?.EnsureScratchFolder(); }
            catch (Exception e) { error = "Output folder error: " + e.Message; return false; }
            if (string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(scratch)) { error = "No output folder set."; return false; }
            string ffmpeg = FfmpegRecorder.ResolveFfmpegPath(Output?.FfmpegPath);
            if (ffmpeg == null) { error = "ffmpeg was not found."; return false; }

            _rawResumeGrab = _isGrabbing;
            if (_isGrabbing) StopGrab();

            // Everything below mutates the board (Bayer OFF), buffers, and the session. Any failure
            // here — especially a MILException from StartGrab — must NOT leave the board in raw mode
            // or leak the session, so the whole sequence is guarded and restores color on fault.
            try
            {
                SetBayerConversion(false);          // board sends raw Bayer band=1 (~3x smaller)
                FreeBuffers(resetBrightness: false); // keep the graph continuous across the color/Bayer switch
                AllocateBuffers(RAW_GRAB_BUFFERS);  // band=1 display + deep band=1 grab ring

                _rawW = (int)MIL.MdigInquire(_digId, MIL.M_SIZE_X, MIL.M_NULL);
                _rawH = (int)MIL.MdigInquire(_digId, MIL.M_SIZE_Y, MIL.M_NULL);
                int band = (int)MIL.MdigInquire(_digId, MIL.M_SIZE_BAND, MIL.M_NULL);

                if (band != 1)
                {
                    // Camera has no Bayer filter (mono) or the board ignored the request — abort safely.
                    error = "This camera does not support raw Bayer (band=1) capture.";
                    RestoreColorAfterRaw();
                    return false;
                }

                int segSecs = Output?.RawSegmentSeconds ?? 60;
                _rawSegments = new RawSegmentSession(ffmpeg, scratch, outDir, SafeName(),
                    _rawW, _rawH, _rawW * _rawH * band, segSecs, InquireBayerPixelFormat());
                _rawDisplayCounter = 0;
                _rawMissed = 0;
                _rawStartTime = DateTime.Now;
                _rawRecording = true;
                StartGrab();
            }
            catch (Exception e)
            {
                error = "Failed to start RAW recording: " + e.Message;
                var s = _rawSegments;
                _rawSegments = null;
                _rawRecording = false;
                if (s != null) { try { s.Finish(); s.WaitConversions(2000); s.Dispose(); } catch { } }
                RestoreColorAfterRaw();   // guarantees Bayer conversion is turned back ON
                return false;
            }

            RaisePropertyChanged(nameof(IsRawRecording));
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(RecordingActive));
            RaisePropertyChanged(nameof(RecordingBannerText));
            return true;
        }

        /// <summary>
        /// Stops the capture and restores color grabbing. The final segment plus any still-pending
        /// segments keep transcoding on the session's background thread; RefreshStats surfaces the
        /// result via <see cref="RawRecordingFinished"/> once they finish.
        /// </summary>
        public void StopRawRecording()
        {
            if (!_rawRecording)
                return;

            if (_isGrabbing) StopGrab();   // stops the hook; safe to finalize the session

            RawSegmentSession s = _rawSegments;
            _rawSegments = null;
            _rawRecording = false;

            if (s != null)
            {
                s.Finish();            // finalize the current segment; conversions continue in background
                _rawFinishing = s;     // RefreshStats polls this to surface completion/errors
                _rawConverting = true;
            }

            RestoreColorAfterRaw();
            RaisePropertyChanged(nameof(StatusText));
        }

        /// <summary>Re-enables color Bayer conversion, restores normal buffers, and resumes grabbing.</summary>
        private void RestoreColorAfterRaw()
        {
            _rawSegments = null;
            _rawRecording = false;
            SetBayerConversion(true);   // critical: do this FIRST so color is restored even if the rest faults
            FreeBuffers(resetBrightness: false); // keep the graph continuous across the color/Bayer switch
            AllocateBuffers(REQUESTED_GRAB_BUFFERS);
            if (_rawResumeGrab) TryStartGrab();   // reports rather than silently swallowing
            RaisePropertyChanged(nameof(IsRawRecording));
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(RecordingActive));
            RaisePropertyChanged(nameof(RecordingBannerText));
        }

        /// <summary>
        /// Maps the digitizer's Bayer mosaic to the matching ffmpeg raw pixel format. MIL names a
        /// pattern by the first two pixels of the first line (M_BAYER_GR = G,R -> GRBG), which is
        /// exactly what ffmpeg's bayer_*8 names encode. Falls back to bayer_rggb8 — the previous
        /// hard-coded value — if the digitizer doesn't report a pattern.
        /// </summary>
        private string InquireBayerPixelFormat()
        {
            const string fallback = "bayer_rggb8";
            if (_digId == MIL.M_NULL)
                return fallback;

            // Cameras without a mosaic have no such setting; probing must not raise a modal dialog.
            MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
            try
            {
                MIL_INT pattern = MIL.MdigInquire(_digId, MIL.M_BAYER_PATTERN, MIL.M_NULL);
                long masked = (long)pattern & MIL.M_BAYER_MASK;   // strip unrelated flag bits
                if (masked == MIL.M_BAYER_RG) return "bayer_rggb8";
                if (masked == MIL.M_BAYER_GR) return "bayer_grbg8";
                if (masked == MIL.M_BAYER_BG) return "bayer_bggr8";
                if (masked == MIL.M_BAYER_GB) return "bayer_gbrg8";
                return fallback;
            }
            catch (MILException)
            {
                return fallback;
            }
            finally
            {
                MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE);
            }
        }

        private string RawStatusSuffix()
        {
            if (_rawRecording && _rawSegments != null)
            {
                var t = DateTime.Now - _rawStartTime;
                string missed = _rawMissed > 0 ? $"  ⚠ missed {_rawMissed}" : "";
                return $"  ● REC RAW seg{_rawSegments.SegmentIndex}  {(int)t.TotalMinutes:00}:{t.Seconds:00}{missed}";
            }
            if (_rawConverting)
                return "  (converting segments → MP4…)";
            return "";
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
            catch (MILException)
            {
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

            SyncRoiInputs();
            RaisePropertyChanged(nameof(SupportsRoi));
            RaisePropertyChanged(nameof(RoiHint));
            RaisePropertyChanged(nameof(Roi));
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

            // Reading a feature name the camera does not expose raises a MIL error (and a modal
            // error dialog) — suppress printing while probing speculative feature names.
            MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
            try
            {
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
                MIL_INT payload = MIL.MdigInquire(_digId, MIL.M_GC_PAYLOAD_SIZE, MIL.M_NULL);
                sb.Append($"  payload={(long)payload}B");
            }
            catch (MILException) { }
            }
            finally
            {
                MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE);
            }

            return sb.ToString();
        }

        private void RefreshExposureReadback()
        {
            if (_supportsExposure && TryGetFeatureDouble(MIL.M_FEATURE_VALUE, F_EXPOSURE_TIME, out double v))
                ExposureInput = v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Parses the exposure input box and applies it to the camera.</summary>
        public bool ApplyExposure()
        {
            if (!_supportsExposure)
                return false;
            if (!double.TryParse(_exposureInput, NumberStyles.Float, CultureInfo.InvariantCulture, out double us))
                return false;

            bool ok = SetFeatureDouble(F_EXPOSURE_TIME, us);
            RefreshExposureReadback();
            return ok;
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

        private string ReadBalanceRatio(string channel)
        {
            if (!FeatureAvailable(F_BALANCE_RATIO))
                return "";
            if (!TrySetFeatureString(F_BALANCE_RATIO_SELECTOR, channel))
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

        private bool SetBalanceRatio(string channel, string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return false;
            if (!TrySetFeatureString(F_BALANCE_RATIO_SELECTOR, channel))
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
            catch (MILException)
            {
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
        /// Reads the sensor bounds and increments so a requested ROI can be snapped to them.
        ///
        /// Prefers the WidthMax/HeightMax features over Width's own M_FEATURE_MAX. Width's maximum
        /// is offset-dependent — with OffsetX already at 1500 it reports what is left of the row,
        /// not the sensor — so reading it while a previous crop is still applied would shrink the
        /// bounds a little more every time a ROI is set. WidthMax/HeightMax are sensor constants.
        /// This camera exposes both (verified 2026-08-20: 2064 x 1544).
        /// </summary>
        private void RefreshRoiBounds()
        {
            if (!_features.TryGetInt(MIL.M_FEATURE_VALUE, F_WIDTH_MAX, out long maxW) || maxW <= 0)
                if (!_features.TryGetInt(MIL.M_FEATURE_MAX, F_WIDTH, out maxW) || maxW <= 0)
                    _features.TryGetInt(MIL.M_FEATURE_VALUE, F_WIDTH, out maxW);
            if (!_features.TryGetInt(MIL.M_FEATURE_VALUE, F_HEIGHT_MAX, out long maxH) || maxH <= 0)
                if (!_features.TryGetInt(MIL.M_FEATURE_MAX, F_HEIGHT, out maxH) || maxH <= 0)
                    _features.TryGetInt(MIL.M_FEATURE_VALUE, F_HEIGHT, out maxH);
            _sensorMaxW = maxW;
            _sensorMaxH = maxH;

            _roiIncX = ReadIncrement(F_OFFSET_X);
            _roiIncY = ReadIncrement(F_OFFSET_Y);
            _roiIncW = ReadIncrement(F_WIDTH);
            _roiIncH = ReadIncrement(F_HEIGHT);
        }

        private int ReadIncrement(string feature) =>
            _features.TryGetInt(MIL.M_FEATURE_INCREMENT, feature, out long inc) && inc > 0
                ? (int)inc
                : 2;   // ChannelRoi.Snap floors this at 2 anyway; 2 is just the honest default

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
        /// Writes <see cref="_roi"/> to the camera. Called from AllocateCamera BEFORE
        /// AllocateBuffers, so M_SIZE_X/M_SIZE_Y are inquired after the ROI has taken effect.
        ///
        /// The write order matters and is not negotiable: offsets go to zero first, then the
        /// sizes, then the real offsets. Setting a size while an old offset is still in place is
        /// rejected whenever offset+size would exceed the sensor, which is exactly what happens
        /// when moving from a small far-right crop to a large one.
        /// </summary>
        private void WriteRoiToCamera()
        {
            if (_digId == MIL.M_NULL || !SupportsRoi)
                return;

            // A rejected feature write prints before it throws, and with printing enabled that
            // print is a MODAL dialog on this thread — it hangs the app rather than failing soft,
            // which is the whole reason SetInt returns a bool. Same trap CLAUDE.md documents for
            // MdigAlloc on an empty port. The restore MUST be in the finally: leaving printing
            // disabled would silently swallow every later MIL error in the process.
            MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
            try
            {
                // Offsets to zero before reading the bounds: Width's maximum is offset-dependent on
                // cameras that do not expose WidthMax, so reading bounds while a previous crop still
                // stands makes them shrink a little on every apply.
                SetIntIfDifferent(F_OFFSET_X, 0);
                SetIntIfDifferent(F_OFFSET_Y, 0);

                RefreshRoiBounds();
                if (_sensorMaxW <= 0 || _sensorMaxH <= 0)
                    return;

                if (_roi.IsFullFrame)
                {
                    SetIntIfDifferent(F_WIDTH, _sensorMaxW);
                    SetIntIfDifferent(F_HEIGHT, _sensorMaxH);
                    return;
                }

                ChannelRoi snapped = _roi.Snap(_roiIncX, _roiIncY, _roiIncW, _roiIncH,
                                               (int)_sensorMaxW, (int)_sensorMaxH);

                bool ok = SetIntIfDifferent(F_WIDTH, snapped.Width);
                ok &= SetIntIfDifferent(F_HEIGHT, snapped.Height);
                ok &= SetIntIfDifferent(F_OFFSET_X, snapped.OffsetX);
                ok &= SetIntIfDifferent(F_OFFSET_Y, snapped.OffsetY);

                if (ok)
                {
                    _roi = snapped;
                    return;
                }

                // A partly-applied ROI is the worst outcome: the picture would look plausibly cropped
                // while the offsets sat at zero, and _roi would assert a crop the camera refused. Put
                // the sensor back to full frame and say so, rather than reporting a crop we do not have.
                SetIntIfDifferent(F_OFFSET_X, 0);
                SetIntIfDifferent(F_OFFSET_Y, 0);
                SetIntIfDifferent(F_WIDTH, _sensorMaxW);
                SetIntIfDifferent(F_HEIGHT, _sensorMaxH);
                _roi = ChannelRoi.FullFrame;
            }
            finally
            {
                MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE);
            }
        }

        /// <summary>
        /// Applies a new acquisition ROI: snaps it to the hardware grid, persists it, and
        /// reallocates the digitizer and buffers. Resumes grabbing if it was active.
        ///
        /// Reallocation is unavoidable — the buffers are sized from the payload, so a crop that
        /// did not resize them would leave MIL writing a smaller frame into a larger buffer.
        /// Mirrors <see cref="ReloadWithDcf"/>, which solves the same problem for the DCF.
        /// </summary>
        public bool ApplyRoi(ChannelRoi requested)
        {
            if (!CameraPresent || !SupportsRoi)
                return false;

            // A bounds read can print too, on a locked node — same trap as WriteRoiToCamera.
            MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_DISABLE);
            try { RefreshRoiBounds(); }
            finally { MIL.MappControl(MIL.M_DEFAULT, MIL.M_ERROR, MIL.M_PRINT_ENABLE); }

            ChannelRoi snapped = requested.IsFullFrame
                ? ChannelRoi.FullFrame
                : requested.Snap(_roiIncX, _roiIncY, _roiIncW, _roiIncH,
                                 (int)_sensorMaxW, (int)_sensorMaxH);

            bool wasGrabbing = _isGrabbing;
            if (wasGrabbing)
                StopGrab();

            Output?.SetRoi(_index, snapped);

            FreeCamera();
            _cameraAvailable = true;
            AllocateCamera();          // re-reads the ROI from Output and writes it to the camera

            if (wasGrabbing)
                TryStartGrab();

            RaisePropertyChanged(nameof(DisplayId));
            RaisePropertyChanged(nameof(Roi));
            RaisePropertyChanged(nameof(RoiHint));
            SyncRoiInputs();
            return CameraPresent;
        }

        /// <summary>Returns to the full sensor.</summary>
        public bool ClearRoi() => ApplyRoi(ChannelRoi.FullFrame);

        /// <summary>Parses the four input boxes and applies them. False if any is not a number.</summary>
        public bool ApplyRoiFromInputs()
        {
            if (!int.TryParse(_roiInputX, out int x) || !int.TryParse(_roiInputY, out int y) ||
                !int.TryParse(_roiInputW, out int w) || !int.TryParse(_roiInputH, out int h))
                return false;
            return ApplyRoi(new ChannelRoi(x, y, w, h));
        }

        /// <summary>Writes the applied (snapped) ROI back into the input boxes, so the operator
        /// sees what the hardware actually took rather than what they typed.</summary>
        private void SyncRoiInputs()
        {
            RoiInputX = _roi.OffsetX.ToString(CultureInfo.InvariantCulture);
            RoiInputY = _roi.OffsetY.ToString(CultureInfo.InvariantCulture);
            RoiInputW = (_roi.IsFullFrame ? 0 : _roi.Width).ToString(CultureInfo.InvariantCulture);
            RoiInputH = (_roi.IsFullFrame ? 0 : _roi.Height).ToString(CultureInfo.InvariantCulture);
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
