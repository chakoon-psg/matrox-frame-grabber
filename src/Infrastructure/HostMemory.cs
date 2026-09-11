using System;
using System.Runtime.InteropServices;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// How much physical memory is free, for the one allocation big enough to need to ask.
    ///
    /// The evidence ring is gigabytes. Asking for more than the machine has does not fail - Windows
    /// pages instead, and paging beside a 2.9 GB/s acquisition is exactly the load measured to cost
    /// frames. So the ring asks first and stays off rather than taking the grab down with it.
    /// </summary>
    public static class HostMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        /// <summary>Free physical memory in bytes, or 0 when it cannot be read.</summary>
        public static long AvailableBytes()
        {
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref m)) return 0L;
                return m.ullAvailPhys > long.MaxValue ? long.MaxValue : (long)m.ullAvailPhys;
            }
            catch { return 0L; }
        }

        /// <summary>
        /// Whether an allocation of <paramref name="want"/> should go ahead.
        ///
        /// Only a share of what is free, because three channels allocate one after another and the
        /// display, the encoders and the grab rings all still have to fit. Unknown free memory
        /// (0) is allowed through - refusing on a failed query would turn a diagnostic into an
        /// outage.
        /// </summary>
        public static bool Fits(long want, long available, double share = 0.6) =>
            want <= 0L || available <= 0L || want <= (long)(available * share);
    }
}
