using System;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string AutoStartSwitch = "--autostart";

        /// <summary>Shortest unattended run. Below this the summary line has no frame rate yet.</summary>
        private const int MinAutoRunSeconds = 2;
        private const int MaxAutoRunSeconds = 3600;

        /// <summary>
        /// Seconds to grab on every present camera before closing by itself, from
        /// <c>--autostart &lt;seconds&gt;</c>. Zero — the default — is an ordinary interactive start.
        ///
        /// This exists for the acceptance measurement, which is a comparison: frames missed with a
        /// change and without it. Done by hand it compares two people's timing as much as the code.
        /// </summary>
        public static int AutoRunSeconds { get; private set; }

        private const string PwmSweepSwitch = "--pwm-sweep";
        private const string PwmRoomSwitch = "--pwm-room";
        private const string PwmScanSwitch = "--pwm-scan";

        /// <summary>
        /// Channel to run the backlight PWM exposure sweep on, from <c>--pwm-sweep CAM0</c>.
        /// Null — the default — is an ordinary start.
        ///
        /// The sweep exists because the manual procedure asked a person to watch a number update
        /// twice a second for fifteen seconds and write down the smallest and largest they saw.
        /// That is the least reliable part of the measurement and the easiest to remove: the app
        /// can take hundreds of readings instead of thirty, and never mis-read one.
        /// </summary>
        public static string PwmSweepChannel { get; private set; }

        /// <summary>
        /// True for the second half of the sweep, taken with the panel switched off to see whether
        /// room lighting reaches the camera and at what frequency. Passed as <c>--pwm-room</c>.
        /// </summary>
        public static bool PwmSweepRoom { get; private set; }

        /// <summary>
        /// Measure the whole ripple-vs-exposure curve instead of testing two hypotheses, from
        /// <c>--pwm-scan</c>. 29 points at 20 s each, so about ten minutes.
        ///
        /// The four-point sweep can only answer "100 Hz family, 120 Hz family, or neither", and
        /// "neither" leaves the operator to go and find the minimum by hand. Scanning reads the
        /// nulls straight off the curve and works for any frequency; it only costs time, and the
        /// time is no longer a person's.
        /// </summary>
        public static bool PwmSweepScan { get; private set; }

        private const string ChannelsSwitch = "--channels";
        private const string ExposureScanSwitch = "--expo-scan";
        private const string DwellSwitch = "--dwell";

        private const int MinDwellSeconds = 5;
        private const int MaxDwellSeconds = 600;
        private const int DefaultDwellSeconds = 30;

        /// <summary>
        /// Exposures to walk, in microseconds, from <c>--expo-scan 10000,8000,6000</c>. Null for an
        /// ordinary run.
        ///
        /// This exists because doing it by hand went wrong: three exposures were set and snapped in
        /// sequence while the cameras were stopped, so every snapshot was the same frozen frame and
        /// the log recorded only the startup exposure. One switch does the whole sequence in the one
        /// order that yields usable data - set, settle, dwell while the measurement log fills,
        /// snapshot, next - and the exposure ends up in both the CSV and the file it belongs to.
        /// </summary>
        public static double[] ExposureScan { get; private set; }

        /// <summary>Seconds to hold each exposure, from <c>--dwell 30</c>.</summary>
        public static int DwellSeconds { get; private set; } = DefaultDwellSeconds;

        private const string StillsSwitch = "--stills";

        /// <summary>
        /// <c>--stills</c>: keep frames in MIL buffers during an <c>--autostart</c> run and write
        /// them as PNG, to measure what a lossless still costs.
        ///
        /// MbufExport with M_PNG already runs in this app for snapshots, so the question is not
        /// whether it works but what it costs and whether the picture survives the round trip:
        /// how long a MIL-to-MIL keep takes inside the acquisition hook, how long the PNG write
        /// takes on the stats tick, and whether the file's mean luma matches what the brightness
        /// meter reported for the same run.
        /// </summary>
        public static bool StillProbe { get; private set; }

        private const string RecordSwitch = "--rec";

        /// <summary>
        /// <c>--rec</c>: record every present camera for the whole of an <c>--autostart</c> run.
        ///
        /// This exists for one measurement. Recording extracts the entire frame inside the
        /// acquisition hook, which is exactly what a preroll ring of frames would have to do, so a
        /// timed run with recording on says whether that extraction fits in the frame period - and
        /// whether the encoder keeps up at the acquisition rate, which decides whether the file's
        /// time axis can be trusted enough to cut an event window out of it.
        /// </summary>
        public static bool RecordDuringAutoRun { get; private set; }

        private const string NoDetectSwitch = "--no-detect";

        /// <summary>
        /// <c>--no-detect</c>: run without the tile reduction and the detector. Exists for one
        /// measurement -- frames missed with detection on the acquisition path against the same run
        /// without it -- which is the acceptance test for putting it there.
        /// </summary>
        public static bool DetectionOff { get; private set; }

        private const string BayerScopeSwitch = "--bayer-scope";
        private const string DecimSwitch = "--decim";

        /// <summary>
        /// Channel indices this process should take a digitizer for, from <c>--channels 0,1</c>.
        /// Null — the default — means all of them.
        ///
        /// This is here to answer whether the board can be split one process per camera. Two full
        /// instances always collide on the same four ports, so without a way to hand each process a
        /// different subset the question cannot be asked at all.
        /// </summary>
        public static System.Collections.Generic.HashSet<int> OwnedChannels { get; private set; }

        /// <summary>
        /// Run the M_BAYER_CONVERSION scope diagnostic and exit, from <c>--bayer-scope</c>.
        /// Answers whether the setting is per-digitizer or board-wide, which decides whether the
        /// channels can be split across processes.
        /// </summary>
        public static bool BayerScopeTest { get; private set; }

        /// <summary>Decimation to apply to owned channels at startup, from <c>--decim 1</c>. 0 = leave alone.</summary>
        public static int StartupDecimation { get; private set; }

        /// <summary>True while a PWM sweep is driving the app.</summary>
        public static bool PwmSweeping => !string.IsNullOrEmpty(PwmSweepChannel);

        /// <summary>True while an exposure scan is driving the app.</summary>
        public static bool ExposureScanning => ExposureScan != null && ExposureScan.Length > 0;

        /// <summary>True while running unattended, so nothing waits for a person who isn't there.</summary>
        public static bool Unattended =>
            AutoRunSeconds > 0 || PwmSweeping || BayerScopeTest || ExposureScanning;

        protected override void OnStartup(StartupEventArgs e)
        {
            AutoRunSeconds = ParseAutoRunSeconds(e.Args);
            PwmSweepChannel = ParseSwitchValue(e.Args, PwmSweepSwitch);
            PwmSweepRoom = HasSwitch(e.Args, PwmRoomSwitch);
            PwmSweepScan = HasSwitch(e.Args, PwmScanSwitch);
            OwnedChannels = ParseChannels(ParseSwitchValue(e.Args, ChannelsSwitch));
            BayerScopeTest = HasSwitch(e.Args, BayerScopeSwitch);
            DetectionOff = HasSwitch(e.Args, NoDetectSwitch);
            RecordDuringAutoRun = HasSwitch(e.Args, RecordSwitch);
            StillProbe = HasSwitch(e.Args, StillsSwitch);
            ExposureScan = ParseExposures(ParseSwitchValue(e.Args, ExposureScanSwitch));
            DwellSeconds = ParseDwellSeconds(e.Args);
            int.TryParse(ParseSwitchValue(e.Args, DecimSwitch), NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out int decim);
            StartupDecimation = decim;

            // Processes sharing a board must not share a log file — the log's lock is process-local,
            // so they would interleave and drop each other's lines. Only a split run gets a suffix,
            // so the ordinary single-process log keeps its name.
            if (OwnedChannels != null)
                MilErrorLog.FileSuffix = "-ch" + string.Join("", OwnedChannels);

            base.OnStartup(e);
        }

        /// <summary>
        /// Reads "10000,8000,6000" into microsecond exposures, keeping the order given: the scan
        /// walks them as written, so a run can go bright-to-dark or dark-to-bright as the operator
        /// intends. Out-of-range and unparseable entries are dropped rather than clamped - a typo
        /// must not silently become a measurement at some other exposure.
        /// </summary>
        private static double[] ParseExposures(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var list = new System.Collections.Generic.List<double>();
            foreach (string part in value.Split(','))
            {
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out double us) && us >= 100.0 && us <= 1e6)
                    list.Add(us);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Reads <c>--dwell 30</c>, clamped; the default is used when absent.</summary>
        private static int ParseDwellSeconds(string[] args)
        {
            if (!int.TryParse(ParseSwitchValue(args, DwellSwitch), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out int seconds))
                return DefaultDwellSeconds;

            return seconds < MinDwellSeconds ? MinDwellSeconds
                 : seconds > MaxDwellSeconds ? MaxDwellSeconds
                 : seconds;
        }

        /// <summary>Reads "0,2" into a set. Null for absent or unparseable, meaning all channels.</summary>
        private static System.Collections.Generic.HashSet<int> ParseChannels(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var set = new System.Collections.Generic.HashSet<int>();
            foreach (string part in value.Split(','))
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                                 out int index))
                    set.Add(index);

            return set.Count > 0 ? set : null;
        }

        /// <summary>Reads <c>--switch value</c> or <c>--switch=value</c>. Null when absent.</summary>
        private static string ParseSwitchValue(string[] args, string name)
        {
            if (args == null) return null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;

                if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = a.Substring(name.Length + 1).Trim();
                    return v.Length == 0 ? null : v;
                }
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string v = (args[i + 1] ?? "").Trim();
                    return v.Length == 0 || v.StartsWith("--", StringComparison.Ordinal) ? null : v;
                }
            }
            return null;
        }

        private static bool HasSwitch(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Reads <c>--autostart 30</c> or <c>--autostart=30</c>. Anything unparseable is ignored
        /// rather than refused: a typo in a switch must not stop the app from opening normally.
        /// </summary>
        private static int ParseAutoRunSeconds(string[] args)
        {
            if (args == null)
                return 0;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null)
                    continue;

                string value = null;
                if (arg.StartsWith(AutoStartSwitch + "=", StringComparison.OrdinalIgnoreCase))
                    value = arg.Substring(AutoStartSwitch.Length + 1);
                else if (arg.Equals(AutoStartSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    value = args[i + 1];

                if (value == null)
                    continue;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                    continue;

                return seconds < MinAutoRunSeconds ? MinAutoRunSeconds
                     : seconds > MaxAutoRunSeconds ? MaxAutoRunSeconds
                     : seconds;
            }

            return 0;
        }

        /// <summary>
        /// Last-resort backstop: a MIL call that throws on the UI thread would otherwise terminate
        /// the process mid-capture. Individual call sites still handle their own failures — this
        /// only keeps an unforeseen one from killing an in-progress recording. It deliberately does
        /// NOT swallow exceptions off the UI thread, which stay fatal.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (Unattended)
            {
                // A modal box here would hold the run until the caller's timeout, and the caller is
                // a script. Record it and close through the window, which is what frees MIL and
                // puts the board's Bayer conversion back.
                MilErrorLog.Note("autostart: unhandled UI exception - " + e.Exception.Message);
                e.Handled = true;
                MainWindow?.Close();
                return;
            }

            MessageBox.Show(e.Exception.Message, "Unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
