using System;
using System.Collections.Generic;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Owns the top-level MIL application and system (the Rapixo CXP board) and creates
    /// one <see cref="CameraChannel"/> per camera. The system is allocated once and shared
    /// by all channels.
    /// </summary>
    public class MilApplicationManager
    {
        // Number of camera views to create (2x2 grid: up to four cameras on one Rapixo CXP board).
        public const int ChannelCount = 4;

        // System descriptors tried in order. The Rapixo CoaXPress board is
        // "M_SYSTEM_RAPIXOCXP"; falling back to the MILConfig default system keeps the
        // app usable on a workstation configured differently (or with a simulator).
        private static readonly string[] SystemDescriptors =
        {
            "M_SYSTEM_RAPIXOCXP",
            "M_SYSTEM_DEFAULT"
        };

        private MIL_ID _appId = MIL.M_NULL;
        private MIL_ID _sysId = MIL.M_NULL;

        private readonly List<CameraChannel> _channels = new List<CameraChannel>();
        public IReadOnlyList<CameraChannel> Channels => _channels;

        /// <summary>App-wide output folder + resolution settings, shared by all channels.</summary>
        public OutputSettings Output { get; } = OutputSettings.Load();

        /// <summary>The system descriptor that was actually allocated (for status display).</summary>
        public string AllocatedSystemDescriptor { get; private set; }

        /// <summary>Number of digitizers the allocated board reports.</summary>
        public long DigitizerCount { get; private set; }

        /// <summary>
        /// Channel indices this process should take a digitizer for. Null — the default — means all
        /// of them, which is the ordinary single-process arrangement.
        ///
        /// This exists to test whether the board can be split across processes: one per camera,
        /// each holding a different digitizer. Without it the question cannot be asked, because two
        /// full instances always collide on the same four ports and prove nothing beyond that.
        /// </summary>
        public HashSet<int> OwnedChannels { get; set; }

        /// <summary>
        /// Allocates the MIL application and system and builds the camera channels.
        /// Throws <see cref="MILException"/> if no system can be allocated.
        /// </summary>
        public void Allocate()
        {
            MIL.MappAlloc(MIL.M_NULL, MIL.M_DEFAULT, ref _appId);
            MIL.MappControl(_appId, MIL.M_ERROR, MIL.M_THROW_EXCEPTION);

            // MIL prints its errors as a MODAL dialog, on whatever thread raised the error. On an
            // acquisition thread that stops the channel until somebody dismisses it, and a dialog
            // hidden behind the main window reads to the operator as "the camera froze". Since
            // M_THROW_EXCEPTION above already delivers every error as a MILException we can catch,
            // the print adds nothing but a blocking dialog. Errors go to MilErrorLog instead.
            MIL.MappControl(_appId, MIL.M_ERROR, MIL.M_PRINT_DISABLE);

            AllocateSystem();

            DigitizerCount = MIL.MsysInquire(_sysId, MIL.M_DIGITIZER_NUM, MIL.M_NULL);

            // Stamped with the process id because several instances can share one log file, and a
            // split-process run is unreadable without knowing which line came from which.
            MilErrorLog.Note($"pid {System.Diagnostics.Process.GetCurrentProcess().Id}: " +
                             $"system {AllocatedSystemDescriptor}, {DigitizerCount} digitizers, " +
                             $"channels {(OwnedChannels == null ? "all" : string.Join(",", OwnedChannels))}");

            ProbeVideoSinks();

            for (int i = 0; i < ChannelCount; i++)
            {
                var channel = new CameraChannel(i);
                channel.Output = Output;
                // A channel outside OwnedChannels gets a pane but no digitizer, exactly like an
                // empty port. That is what lets one process hold a subset of the board's channels,
                // which is the only way to answer whether several processes can share it.
                bool owned = OwnedChannels == null || OwnedChannels.Contains(i);
                channel.Allocate(_sysId, cameraAvailable: owned && i < DigitizerCount,
                                 dcfName: "M_DEFAULT");
                _channels.Add(channel);
            }
        }

        private void AllocateSystem()
        {
            foreach (string descriptor in SystemDescriptors)
            {
                try
                {
                    MIL.MsysAlloc(_appId, descriptor, MIL.M_DEV0, MIL.M_DEFAULT, ref _sysId);
                }
                catch (MILException)
                {
                    _sysId = MIL.M_NULL;
                }

                if (_sysId != MIL.M_NULL)
                {
                    AllocatedSystemDescriptor = descriptor;
                    return;
                }
            }

            throw new InvalidOperationException(
                "No MIL system could be allocated. Check that the Rapixo CXP board is " +
                "installed and selected as the default system in MILConfig.");
        }

        /// <summary>
        /// Starts acquisition on every channel that has a camera. One channel failing to start
        /// must not abort the others (or take the app down), so this uses the non-throwing form;
        /// each failure is surfaced through that channel's GrabFailed event.
        /// </summary>
        public void StartAll()
        {
            foreach (var channel in _channels)
                channel.TryStartGrab();
        }

        /// <summary>Stops acquisition on every channel.</summary>
        public void StopAll()
        {
            foreach (var channel in _channels)
                channel.StopGrab();
        }

        /// <summary>Frees all MIL resources in reverse order of allocation.</summary>
        /// <summary>
        /// Records which MIL modules this installation is licensed for, and whether the board has
        /// its own encoder.
        ///
        /// Both were measured once and written into docs/adr/0001-ffmpeg-for-all-encoding.md, which
        /// is where the decision to encode only through ffmpeg comes from. A document cannot notice
        /// when it stops being true - a licence bought later, or a different board, would leave the
        /// app still routing everything through ffmpeg for a reason that had expired. Logging it
        /// each start costs two inquiries and keeps the premise checkable.
        /// </summary>
        /// <summary>
        /// Whether MIL can encode video on this installation, decided once at startup.
        ///
        /// The recording path needs a yes or no, not a diagnosis: with a yes it can offer the MIL
        /// sink, with a no it falls through to ffmpeg, and either way the log says which and why so
        /// a disabled button is never unexplained. Why the answer is what it is belongs to whoever
        /// supplies the board - see docs/adr/0001-ffmpeg-for-all-encoding.md.
        /// </summary>
        public static bool MilVideoAvailable { get; private set; }

        private static string _milVideoReason = "not probed";

        private void ProbeVideoSinks()
        {
            try
            {
                MIL_INT boardType = _sysId != MIL.M_NULL
                    ? MIL.MsysInquire(_sysId, MIL.M_BOARD_TYPE, MIL.M_NULL)
                    : 0;

                MIL_ID seq = MIL.M_NULL;
                try
                {
                    MIL.MseqAlloc(MIL.M_DEFAULT, MIL.M_DEFAULT, MIL.M_SEQ_COMPRESS,
                                  unchecked((uint)MIL.M_DEFAULT), MIL.M_DEFAULT, ref seq);
                    MilVideoAvailable = seq != MIL.M_NULL;
                    // MseqAlloc returns M_NULL rather than throwing, and errors are print-disabled
                    // process-wide, so the reason has to be read out deliberately or it is lost.
                    _milVideoReason = MilVideoAvailable ? "ok" : CurrentMilError();
                }
                catch (MILException e)
                {
                    MilVideoAvailable = false;
                    _milVideoReason = e.Message.Trim();
                }
                finally
                {
                    try { if (seq != MIL.M_NULL) MIL.MseqFree(seq); } catch { }
                }

                MilErrorLog.Note($"board type 0x{(long)boardType:x}; MIL video sink {(MilVideoAvailable ? "available" : "unavailable - " + _milVideoReason)}");
            }
            catch (MILException e)
            {
                MilErrorLog.Write("probe the video sinks", e);
            }
        }


        /// <summary>
        /// MIL's current error as text. Errors are print-disabled process-wide so a failure that
        /// does not throw leaves nothing anywhere unless it is read out deliberately.
        /// </summary>
        private static string CurrentMilError()
        {
            try
            {
                MIL_INT code = 0;
                MIL.MappGetError(MIL.M_DEFAULT, MIL.M_CURRENT, ref code);

                var msg = new System.Text.StringBuilder(MIL.M_ERROR_MESSAGE_SIZE);
                MIL.MappGetError(MIL.M_DEFAULT, MIL.M_CURRENT + MIL.M_MESSAGE, msg);
                var sub = new System.Text.StringBuilder(MIL.M_ERROR_MESSAGE_SIZE);
                MIL.MappGetError(MIL.M_DEFAULT, MIL.M_CURRENT_SUB_1 + MIL.M_MESSAGE, sub);

                string a = msg.ToString().Trim();
                string b = sub.ToString().Trim();
                return $"MIL error 0x{(long)code:x}: {a}" + (b.Length > 0 ? " / " + b : string.Empty);
            }
            catch (MILException e) { return "could not read the MIL error: " + e.Message.Trim(); }
        }


        public void Free()
        {
            foreach (var channel in _channels)
                channel.Free();
            _channels.Clear();

            if (_sysId != MIL.M_NULL)
            {
                MIL.MsysFree(_sysId);
                _sysId = MIL.M_NULL;
            }

            if (_appId != MIL.M_NULL)
            {
                MIL.MappFree(_appId);
                _appId = MIL.M_NULL;
            }
        }
    }
}
