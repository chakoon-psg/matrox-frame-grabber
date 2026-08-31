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
