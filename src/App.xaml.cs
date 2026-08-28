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

        /// <summary>True while a PWM sweep is driving the app.</summary>
        public static bool PwmSweeping => !string.IsNullOrEmpty(PwmSweepChannel);

        /// <summary>True while running unattended, so nothing waits for a person who isn't there.</summary>
        public static bool Unattended => AutoRunSeconds > 0 || PwmSweeping;

        protected override void OnStartup(StartupEventArgs e)
        {
            AutoRunSeconds = ParseAutoRunSeconds(e.Args);
            PwmSweepChannel = ParseSwitchValue(e.Args, PwmSweepSwitch);
            PwmSweepRoom = HasSwitch(e.Args, PwmRoomSwitch);
            base.OnStartup(e);
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
